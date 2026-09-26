using System.Runtime.ExceptionServices;
using System.Text;
using Grpc.Core;
using Grpc.Net.Client;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;

namespace CopilotHive.Worker;

/// <summary>
/// Core worker lifecycle: register, heartbeat, stream tasks, execute, report.
/// Implements <see cref="IToolCallBridge"/> so custom tools can communicate
/// with the orchestrator mid-task via the existing bidirectional gRPC stream.
/// </summary>
public sealed class WorkerService(
    string orchestratorUrl,
    string workerId,
    string[] capabilities,
    string configRepoDir = "/config-repo") : IToolCallBridge, ISessionClient, IDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly IAgentRunner _agentRunner = new SharpCoderRunner(configRepoDir);
    private readonly string _configRepoDir = configRepoDir;
    private readonly WorkerLogger _log = new("Worker");

    /// <summary>
    /// THE WORKER PROCESS'S ENVIRONMENT PROVENANCE. The <c>Program.cs</c> attempt loop creates ONE
    /// of these OUTSIDE the loop and passes that EXACT object to EVERY service it constructs, so the
    /// operator snapshot and the provisioned-variable tracking survive across the process's
    /// SEQUENTIAL connection attempts: an attempt that applied server-provisioned values can never
    /// be re-read by a later attempt as if the operator had supplied them.
    /// <para>
    /// The OTHER public constructor leaves this as a FRESH, ISOLATED object, which is the previous
    /// per-service behavior and keeps every existing caller (and focused fixture) unchanged. Only
    /// provenance is shared; identity, client, stream, provisioner and response provenance all stay
    /// per-connection.
    /// </para>
    /// </summary>
    private readonly WorkerProvisioningEnvironment _provisioningEnvironment = new();

    /// <summary>
    /// THE ATTEMPT-CONSTRUCTION PATH. <c>Program.cs</c> creates ONE
    /// <see cref="WorkerProvisioningEnvironment"/> OUTSIDE its retry loop and builds EVERY attempt's
    /// service through here, passing that exact object — so the operator snapshot and the
    /// provisioned-variable tracking are the PROCESS's, shared by all of its SEQUENTIAL attempts,
    /// while everything else (runner, send gate, heartbeat, connection) stays per-attempt.
    /// <para>
    /// The state object is taken AS IS: no delegate override is accepted here, so this path cannot
    /// be pointed at a second reader/writer for the same process.
    /// </para>
    /// </summary>
    /// <param name="orchestratorUrl">The orchestrator's gRPC endpoint.</param>
    /// <param name="workerId">The locally configured worker id.</param>
    /// <param name="capabilities">The worker's advertised capabilities.</param>
    /// <param name="provisioningEnvironment">The worker process's shared environment provenance.</param>
    /// <param name="configRepoDir">The config repository directory for this attempt.</param>
    /// <exception cref="ArgumentNullException"><paramref name="provisioningEnvironment"/> is <c>null</c>.</exception>
    internal WorkerService(
        string orchestratorUrl,
        string workerId,
        string[] capabilities,
        WorkerProvisioningEnvironment provisioningEnvironment,
        string configRepoDir = "/config-repo")
        : this(orchestratorUrl, workerId, capabilities, configRepoDir)
    {
        _provisioningEnvironment = provisioningEnvironment
            ?? throw new ArgumentNullException(nameof(provisioningEnvironment));
    }

    // Pending tool calls awaiting orchestrator responses are owned by the CONNECTION their request
    // was written on (WorkerConnection's per-connection response registry), never by this service:
    // the lifetime of such a wait is the lifetime of the connection that can still deliver its
    // response.

    // Current task state — read by heartbeat, written by message loop
    private volatile string? _currentTaskId;
    private volatile string? _currentRole;

    /// <summary>
    /// THE SERVICE'S ONE PUBLISHED CONNECTION — an ACCEPTED registration together with its opened
    /// duplex work stream, the gRPC client for that registration and the provisioner associated with
    /// them. Every production operation SNAPSHOTS this one reference, once, before it awaits, so it
    /// can never pair one registration's writer with another's identity or client.
    /// <para>
    /// Publication and unpublishing happen ONLY through <see cref="PublishConnection"/> and
    /// <see cref="UnpublishConnection"/>, both by REFERENCE IDENTITY: a rejected or partially built
    /// attempt is never published, and a teardown clears exactly the connection it owns. Concurrent
    /// reconnect is NOT enabled, so at most one connection is ever live for this service.
    /// </para>
    /// </summary>
    private WorkerConnection? _connection;

    /// <summary>
    /// TEST SEAM — builds the gRPC call invoker the orchestrator client is constructed from.
    /// <c>null</c> selects the real <see cref="GrpcChannel"/> for <c>orchestratorUrl</c>. A test
    /// supplies a fake invoker so the REAL <see cref="RunAsync"/> can be driven without a live
    /// server.
    /// </summary>
    internal Func<CallInvoker>? CallInvokerFactory { get; set; }

    /// <summary>
    /// TEST SEAM — opens the duplex work stream for a client. <c>null</c> selects the real
    /// <c>client.WorkStream(cancellationToken: ct)</c>. Together with
    /// <see cref="CallInvokerFactory"/> this lets a test hand <see cref="RunAsync"/> a fake client
    /// and a fake duplex stream, without any live server.
    /// </summary>
    internal Func<
        HiveOrchestrator.HiveOrchestratorClient,
        CancellationToken,
        AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>>? WorkStreamFactory { get; set; }

    /// <summary>
    /// TEST SEAM — the HEARTBEAT TASK at its existing launch point. When non-null,
    /// <see cref="RunAsync"/> obtains its <c>heartbeatTask</c> from here, receiving the ACTUAL owned
    /// heartbeat <see cref="CancellationTokenSource"/>; when <c>null</c>, the existing
    /// <see cref="RunHeartbeatAsync"/> loop with its unchanged 30-second
    /// <see cref="PeriodicTimer"/> is used.
    /// <para>
    /// This is a TASK replacement ONLY — neither a supervisor, nor a timer framework, nor operator
    /// configuration, and production never sets it. <see cref="RunAsync"/> owns cancellation,
    /// joining and source disposal IDENTICALLY for both branches, so a controlled task is joined
    /// exactly like the production loop: it is the same <c>heartbeatTask</c> slot the teardown
    /// awaits, and it is never abandoned. The controlled task is not required to be — and must not
    /// be described as — a real heartbeat RPC, and no timer tick is ever awaited because of it.
    /// </para>
    /// </summary>
    internal Func<WorkerConnection, CancellationTokenSource, Task>? HeartbeatTaskFactory { get; set; }

    /// <summary>
    /// TEST SEAM — the instant INSIDE <see cref="DeliverCarriedAssignmentAsync"/> immediately BEFORE
    /// its carried <c>Complete</c> write, receiving the ASSIGNMENT token that the send then uses.
    /// <c>null</c> in production, where the instant contains nothing at all.
    /// </summary>
    /// <remarks>
    /// It exists so a test can observe and control whether the carried delivery is about to write —
    /// and with which token — without any timer, sleep or artificial barrier in production code. It
    /// is awaited, so a blocking hook holds the delivery exactly there; it is never invoked when
    /// unset.
    /// </remarks>
    internal Func<CancellationToken, Task>? CarriedBeforeCompleteSendHook { get; set; }

    /// <summary>
    /// TEST SEAM — the instant INSIDE <see cref="DeliverCarriedAssignmentAsync"/> AFTER its carried
    /// Complete write succeeded and BEFORE its Ready claim. <c>null</c> in production, where the
    /// instant contains nothing at all.
    /// </summary>
    /// <remarks>
    /// It exists so a test can hold the delivery exactly between "result delivered" and "Ready
    /// claimed", which is the window in which a successor assignment's authorization is decided. It
    /// is awaited, so a blocking hook holds the delivery there; it is never invoked when unset.
    /// </remarks>
    internal Func<Task>? CarriedBeforeReadyClaimHook { get; set; }

    /// <summary>
    /// THE CLOCK SEAM FOR COMPLETION RETRANSMISSION — the ONLY test hook this behavior adds, and the
    /// ONLY clock production reads for the retry wait. It defaults to
    /// <see cref="System.TimeProvider.System"/>, is read exactly ONCE per assignment (the value is
    /// snapshotted into the assignment when the assignment is built), and is used for NOTHING else:
    /// the retransmission delay is its only caller, through the
    /// <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> overload, so no heartbeat,
    /// drain, gate or readiness timing becomes dependent on it.
    /// </summary>
    internal TimeProvider TimeProvider { get; set; } = System.TimeProvider.System;

    /// <summary>The currently published connection, or <c>null</c> when none is published.</summary>
    private WorkerConnection? CurrentConnection => Volatile.Read(ref _connection);

    /// <summary>
    /// PUBLISHES a FULLY CONSTRUCTED connection — registration accepted, stream open, provisioner
    /// associated. Called from <see cref="RunAsync"/> BEFORE the initial Ready and before any
    /// assignment processing.
    /// </summary>
    /// <remarks>
    /// Also the seam the direct-loop fixtures use to install the connection they drive the real
    /// message loop with (they construct the <see cref="WorkerConnection"/> themselves). It is
    /// deliberately the ONLY way the published connection can change: publication stays a single,
    /// auditable transition, and concurrent reconnect remains unsupported.
    /// </remarks>
    internal void PublishConnection(WorkerConnection connection) =>
        Volatile.Write(ref _connection, connection);

    /// <summary>
    /// UNPUBLISHES exactly the expected connection, by REFERENCE IDENTITY — never by worker ID, so a
    /// stale teardown can never drop a different registration's connection. A no-op when the
    /// published connection is already something else (or nothing).
    /// </summary>
    /// <remarks>
    /// It also CLEARS the ADOPTION publication when that publication belongs to
    /// <paramref name="expected"/> (see <see cref="ClearAdoption"/>), so the ONE identity-checked
    /// teardown that removes the connection also removes the adoption record of it: a retired
    /// adopted connection can never be handed to a waiter, and no second lifecycle hook is needed.
    /// </remarks>
    private void UnpublishConnection(WorkerConnection expected)
    {
        ClearAdoption(expected);
        Interlocked.CompareExchange(ref _connection, null, expected);
    }

    // ── Adoption publication ────────────────────────────────────────────────────
    //
    // A SECOND, DELIBERATELY SEPARATE PUBLICATION from the connection above. The connection
    // publication says "this is the registration this service currently works on"; the ADOPTION
    // publication says "this is the connection a CARRIED assignment is now allowed to deliver its
    // retained result on". They change at different moments and are consumed by different code, so
    // they are kept apart: the reconnect path publishes an adoption only when the orchestrator
    // ACCEPTED the carried task, and the carried delivery is the ONLY consumer.

    /// <summary>
    /// ONE ADOPTED CONNECTION together with the STREAM TOKEN of the run that adopted it — the token
    /// the carried <c>Ready</c> must be written with, exactly like
    /// <see cref="OrdinaryReadySlot.StreamToken"/>.
    /// </summary>
    private sealed record AdoptedConnection(WorkerConnection Connection, CancellationToken StreamToken);

    /// <summary>
    /// THE MOST RECENTLY ADOPTED CONNECTION together with that run's stream token, or <c>null</c>
    /// while nothing is adopted. At most ONE is retained, so an adoption can never accumulate
    /// history, and an identity-checked teardown clears exactly the connection it owns.
    /// </summary>
    private AdoptedConnection? _adopted;

    /// <summary>
    /// THE ADOPTION-CHANGE SIGNAL — the way a waiter parks until the adoption publication changes.
    /// It is REPLACED on every change (a new adoption, or the clear that follows the adopted
    /// connection's retirement), so every waiter re-evaluates its own condition exactly ONCE per
    /// change: there is no polling, no timer and no spin.
    /// </summary>
    private TaskCompletionSource _adoptionChanged =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The lock serializing the adoption publication, its signal swap and its clear.</summary>
    private readonly object _adoptionLock = new();

    /// <summary>
    /// ADOPTION PUBLICATION — the carried assignment's result may now be delivered on
    /// <paramref name="connection"/>, written with the ADOPTING run's <paramref name="streamToken"/>.
    /// Called by the run that ADOPTED a carried assignment, as its LAST step before the message loop
    /// (in place of the initial Ready).
    /// </summary>
    /// <remarks>
    /// <para>
    /// It REPLACES the previous adoption rather than adding one: only the most recently adopted
    /// connection can be waited for, which is exactly what the carried delivery needs — one delivery
    /// target, no queue and no per-adoption bookkeeping. Every waiter is woken by the signal swap, so
    /// a wait that started before this call re-evaluates and observes the new adoption.
    /// </para>
    /// <para>
    /// Nothing here is inferred from a registration answer: the caller publishes only after the
    /// orchestrator ACCEPTED the carried task, and a rejected or non-adopted registration never
    /// reaches this method.
    /// </para>
    /// </remarks>
    /// <param name="connection">The connection that will carry the carried assignment's delivery.</param>
    /// <param name="streamToken">The stream token of the ADOPTING run.</param>
    internal void PublishAdoption(WorkerConnection connection, CancellationToken streamToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        TaskCompletionSource woken;
        lock (_adoptionLock)
        {
            _adopted = new AdoptedConnection(connection, streamToken);
            woken = ReplaceAdoptionSignal_Locked();
        }

        // OUTSIDE the lock: a waiter's continuation may publish or clear an adoption itself.
        woken.TrySetResult();
    }

    /// <summary>
    /// CLEARS the adoption publication when it belongs to <paramref name="expected"/>, by REFERENCE
    /// IDENTITY — the adoption half of <see cref="UnpublishConnection"/>, and a no-op for any other
    /// connection (or none).
    /// </summary>
    /// <remarks>
    /// Retirement is the end of an adopted connection's usability, so the record of it is dropped
    /// with it and every waiter re-evaluates exactly once. Without the clear, a parked carried
    /// delivery could be handed a connection whose stream is already gone. It never throws.
    /// </remarks>
    /// <param name="expected">The connection whose adoption record is being retired.</param>
    private void ClearAdoption(WorkerConnection expected)
    {
        TaskCompletionSource woken;
        lock (_adoptionLock)
        {
            if (!ReferenceEquals(_adopted?.Connection, expected))
                return;

            _adopted = null;
            woken = ReplaceAdoptionSignal_Locked();
        }

        woken.TrySetResult();
    }

    /// <summary>
    /// Swaps in a FRESH adoption signal and returns the previous one for the caller to complete
    /// outside the lock. Must be called with <see cref="_adoptionLock"/> held.
    /// </summary>
    private TaskCompletionSource ReplaceAdoptionSignal_Locked()
    {
        var previous = _adoptionChanged;
        _adoptionChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return previous;
    }

    /// <summary>
    /// WAITS for an adopted connection OTHER THAN <paramref name="lastTried"/> — the carrying
    /// assignment's delivery target. The wait PARKS rather than polls: it reads the publication under
    /// the lock, and when the current one is not usable it awaits the next change of that
    /// publication, then re-evaluates.
    /// </summary>
    /// <remarks>
    /// NON-THROWING apart from the caller's own cancellation: a cleared publication is simply "not
    /// yet", never an error, and a retired adopted connection is never handed out. Because the
    /// publication is replaced (not removed) on each change, a waiter that re-evaluates cannot spin
    /// on a signal it has already consumed.
    /// </remarks>
    /// <param name="lastTried">The connection the caller already tried, or <c>null</c> for the first wait.</param>
    /// <param name="ct">The ASSIGNMENT token — cancelling it ends the wait.</param>
    private async Task<AdoptedConnection> AwaitAdoptedConnectionAsync(
        WorkerConnection? lastTried, CancellationToken ct)
    {
        while (true)
        {
            Task changed;
            lock (_adoptionLock)
            {
                var candidate = _adopted;
                if (candidate is not null
                    && !ReferenceEquals(candidate.Connection, lastTried)
                    && !candidate.Connection.IsRetired)
                {
                    return candidate;
                }

                changed = _adoptionChanged.Task;
            }

            await changed.WaitAsync(ct);
        }
    }

    /// <summary>
    /// CHECKED ACCESS — the published connection, or the EXISTING disconnected error when none is
    /// published. A RETIRED connection is rejected too, so a new operation can never start transport
    /// on a connection that teardown has already retired.
    /// </summary>
    private WorkerConnection RequireConnection() =>
        (CurrentConnection ?? throw new InvalidOperationException(WorkerConnection.DisconnectedMessage))
        .EnsureUsable();

    /// <summary>
    /// THE OUTBOUND SEND GATE. gRPC's <see cref="IAsyncStreamWriter{T}"/> allows at most ONE
    /// pending <c>WriteAsync</c> at a time, yet several independent producers write to the work
    /// stream: the assignment body's completion, the tool-call bridge (progress, narrative and
    /// response-bearing calls, invoked from agent turns on arbitrary threads), the initial Ready
    /// and every assignment/cancel Ready. This per-instance semaphore serializes the AWAITED
    /// write itself — not merely message construction — so at most one underlying write is ever
    /// outstanding for this service's active connection.
    /// <para>
    /// Deliberately NOT disposed: callers may be parked in <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/>
    /// or unwinding through their <c>finally</c> release while <see cref="Dispose"/> runs, and
    /// disposing underneath them would introduce an <see cref="ObjectDisposedException"/> failure
    /// mode (or force a blocking drain). The gate holds no unmanaged resource and no wait handle
    /// is ever materialised, so leaving it to the GC is safe and keeps it valid until callers
    /// unwind. It grants NO ordering promise across simultaneous producers beyond serialization,
    /// and NO reconnect, buffering or retry semantics.
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    /// <summary>
    /// TEST SEAM — overrides the connection's provisioner. When the resulting connection carries
    /// NO provisioner the per-assignment config-repo preparation is SKIPPED entirely (no probe, no
    /// clone, no askpass helper, no seam) and the executor is built with the legacy public
    /// constructor.
    /// </summary>
    internal WorkerConfigProvisioner? TestProvisioner { get; set; }

    /// <summary>TEST SEAM — creates the askpass helper directory. <c>null</c> selects the real implementation.</summary>
    internal Action<string>? AskpassDirCreate { get; set; }

    /// <summary>TEST SEAM — writes the askpass helper script. <c>null</c> selects the real implementation.</summary>
    internal Action<string>? AskpassScriptWrite { get; set; }

    /// <summary>
    /// TEST SEAM — applies the owner-only mode to ONE path per call. <c>null</c> selects the real
    /// implementation (<see cref="File.SetUnixFileMode(string, UnixFileMode)"/>).
    /// </summary>
    internal Action<string>? AskpassChmod { get; set; }

    /// <summary>
    /// TEST SEAM — decides whether the chmod step runs at all. <c>null</c> selects the real
    /// platform predicate (<c>!OperatingSystem.IsWindows()</c>).
    /// </summary>
    internal Func<bool>? AskpassChmodPlatform { get; set; }

    /// <summary>The askpass helper script's file name inside its own private directory.</summary>
    private const string AskpassScriptName = "askpass.sh";

    /// <summary>
    /// The EXACT askpass helper script. It implements git's <c>$1</c>-prompt protocol and is
    /// TOKEN-FREE: a username prompt (matching <c>*sername*</c>) answers with the fixed
    /// <c>x-access-token</c> principal, every other prompt reads the credential from the
    /// environment variable the seam injects for the final, credential-carrying launch.
    /// </summary>
    private const string AskpassScriptContent =
        "#!/bin/sh\n"
        + "case \"$1\" in\n"
        + "  *sername*) printf '%s' \"x-access-token\" ;;\n"
        + "  *) printf '%s' \"$GITHUB_CONFIG_REPO_TOKEN\" ;;\n"
        + "esac\n";

    #region Service lifecycle guard (Idle / Running / Disposed)

    /// <summary>The service owns no run: a new <see cref="RunAsync"/> may claim it, or <see cref="Dispose"/> may retire it.</summary>
    private const int LifecycleIdle = 0;

    /// <summary>A run currently owns the service's single runner. Nothing else may claim it, and it may not be disposed yet.</summary>
    private const int LifecycleRunning = 1;

    /// <summary>Final disposal has been CLAIMED (before the fallible runner disposal ran). Terminal and one-way.</summary>
    private const int LifecycleDisposed = 2;

    /// <summary>
    /// THE SERVICE'S ONE ATOMIC LIFECYCLE STATE — an <c>int</c> read and claimed with the
    /// <see cref="Interlocked"/> helpers so exactly one caller can win a transition. It deliberately
    /// carries no queue, no waiter list and no blocking drain: the contract is a single
    /// Idle → Running claim per run, back to Idle at quiescence, and one terminal Idle → Disposed.
    /// </summary>
    private int _lifecycleState = LifecycleIdle;

    /// <summary>
    /// CLAIMS the run guard for one invocation of <see cref="RunAsync"/>: Idle → Running, or a
    /// fail-fast rejection. Called BEFORE the agent runner is prepared, so an overlapping run can
    /// never touch the shared runner.
    /// </summary>
    /// <remarks>
    /// The read-then-claim loop is a retry of the SAME claim, never a wait: whichever thread loses the
    /// <see cref="Interlocked.CompareExchange(ref int, int, int)"/> re-reads the state and reports the
    /// matching typed failure on that same call, so neither rejection depends on timing.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Another run currently owns the service.</exception>
    /// <exception cref="ObjectDisposedException">The service has been finally disposed.</exception>
    private void ClaimRunGuard()
    {
        while (true)
        {
            var observed = Volatile.Read(ref _lifecycleState);

            if (observed == LifecycleDisposed)
            {
                throw new ObjectDisposedException(
                    nameof(WorkerService),
                    "The worker service has been disposed and cannot run again.");
            }

            if (observed == LifecycleRunning)
            {
                throw new InvalidOperationException(
                    "A run is already in progress on this worker service — runs must be sequential.");
            }

            if (Interlocked.CompareExchange(ref _lifecycleState, LifecycleRunning, LifecycleIdle) == LifecycleIdle)
                return;
        }
    }

    /// <summary>
    /// RELEASES the run guard back to Idle: Running → Idle. A no-op when the state is anything else,
    /// so a release that races a completed final disposal can never revive a disposed service.
    /// </summary>
    private void ReleaseRunGuard() =>
        Interlocked.CompareExchange(ref _lifecycleState, LifecycleIdle, LifecycleRunning);

    /// <summary>The sanitized report message for a failed provisioning-callback detachment.</summary>
    private const string ProvisionerDetachmentFailedMessage = "Provisioner detachment failed";

    /// <summary>
    /// DETACHES the run's provisioning callback from the shared runner, ONCE the run's execution and
    /// heartbeat have reached quiescence (this runs after the whole invocation body, including its
    /// lexical transport disposal, has finished or faulted) and BEFORE the run guard is released.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY IT IS REQUIRED. The installed callback is the retired connection's OWN checked entry point.
    /// Leaving it installed would let a LATER sequential run's first lazy client creation provision
    /// through a connection that no longer exists — the callback fails disconnected, or worse, is
    /// silently retargeted. Detaching with the EXISTING <see cref="IAgentRunner.SetConfigProvisioner"/>
    /// member (passing <c>null</c>) keeps the callback's own binding untouched: an already CAPTURED
    /// callback stays bound to the retired connection it came from and is never pointed anywhere else.
    /// </para>
    /// <para>
    /// ERROR PRECEDENCE. A detachment failure is fallible runner interaction, so it must never skip
    /// lexical cleanup (it cannot: the cleanup already ran) and must never replace a prior run/cleanup
    /// failure. With <paramref name="primaryFailure"/> already in flight the detachment failure is
    /// merely REPORTED through the guarded sanitized log — the same existing diagnostics path the
    /// heartbeat-cleanup failures use, with type classification only, never a raw message — and the
    /// primary propagates unchanged. Without a primary it propagates with its ORIGINAL evidence.
    /// </para>
    /// </remarks>
    /// <param name="primaryFailure">The run failure already propagating, or <c>null</c>.</param>
    private void DetachProvisioner(Exception? primaryFailure)
    {
        try
        {
            _agentRunner.SetConfigProvisioner(null);
        }
        catch (Exception ex)
        {
            if (primaryFailure is not null)
            {
                ReportIfPresent(ex, ProvisionerDetachmentFailedMessage);
                return;
            }

            ExceptionDispatchInfo.Capture(ex).Throw();
        }
    }

    #endregion

    /// <summary>
    /// Runs the full worker lifecycle: connects to Copilot, registers with the orchestrator,
    /// opens a bidirectional gRPC stream, and processes task assignments until cancelled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE RETURNED <see cref="WorkerRunOutcome"/> DISTINGUISHES THE TWO CLEAN FINISHES that a
    /// caller previously could not tell apart: a REJECTED registration (which builds and publishes
    /// nothing) and an ACCEPTED registration whose work stream ended with the whole run cleanup —
    /// including this method's lexical transport disposal — completing normally.
    /// </para>
    /// <para>
    /// The outcome is deliberately NOT an inference layer. Every exception that escaped before this
    /// change still escapes unchanged, with the same identity and precedence, so a returned value
    /// means only that no failure left this method: it says nothing about remote shutdown intent,
    /// task success, the absence of a concurrent cancellation request, retryability, or a failure
    /// reason — and no synthetic shutdown state is introduced.
    /// </para>
    /// <para>
    /// ONE RUN AT A TIME, ONE DISPOSAL AT THE VERY END. This method CLAIMS the service's single
    /// Idle → Running run guard BEFORE the agent runner is prepared, so an OVERLAPPING invocation
    /// fails fast with <see cref="InvalidOperationException"/> instead of racing the same runner, and
    /// an invocation after final disposal fails with <see cref="ObjectDisposedException"/>. No runner
    /// is ever created or replaced: the SAME readonly <see cref="IAgentRunner"/> serves every
    /// sequential run of this service.
    /// </para>
    /// <para>
    /// The guard is released back to Idle in this method's OUTERMOST <c>finally</c> — after the whole
    /// invocation body (see <see cref="RunCoreAsync"/>), INCLUDING its lexical stream/channel
    /// disposal, has finished or faulted — so exactly one run can own the runner at a time and a
    /// follow-up sequential run is admitted only once the previous one is fully quiescent.
    /// </para>
    /// <para>
    /// The runner's provisioning callback is DETACHED here as well, AFTER that quiescence and BEFORE
    /// the guard is released, on EVERY path — including early setup failures and the paths that never
    /// reached the installation at all. A callback bound to a connection this run has retired must
    /// never be inherited by a later run (it would provision through a dead connection), and detaching
    /// strictly before the release guarantees a follow-up run cannot have ITS OWN callback cleared by
    /// its predecessor's teardown.
    /// </para>
    /// </remarks>
    /// <param name="ct">Cancellation token that stops the worker.</param>
    /// <returns>
    /// <see cref="WorkerRunOutcome.RegistrationRejected"/> for a rejected registration, or
    /// <see cref="WorkerRunOutcome.WorkStreamEnded"/> once the accepted connection's work stream
    /// ended and the entire lifecycle teardown completed.
    /// </returns>
    /// <exception cref="InvalidOperationException">Another run is already in progress on this service.</exception>
    /// <exception cref="ObjectDisposedException">This service has been finally disposed.</exception>
    public async Task<WorkerRunOutcome> RunAsync(CancellationToken ct)
    {
        ClaimRunGuard();

        // The run's PRIMARY failure, recorded by rethrowing UNCHANGED — the caller still observes the
        // ORIGINAL instance and stack trace — while the OUTERMOST `finally` below applies the existing
        // error-precedence rule to the detachment failure.
        Exception? primaryFailure = null;
        try
        {
            return await RunCoreAsync(ct);
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
            throw;
        }
        finally
        {
            // THE EXIT RE-CHECK — the ONE post-run ownership check, and the ONLY one. It runs HERE,
            // in this `finally`, i.e. strictly AFTER <see cref="RunCoreAsync"/> has returned or thrown
            // and therefore after that method's lexical `using var stream` / `using var ownedChannel`
            // disposal has completed: a pending write of the previous run's stream has been cancelled
            // by that disposal, so joining the carried delivery can no longer wait on a transport that
            // is still parked.
            //
            // A RETAINED assignment whose state is NOT Carried is finished here — which covers the
            // DELIVERED case (its delivery completed and was retained so a successor could be
            // authorized by its started Ready) and, defensively, any state a partially unwound run
            // left behind. A CARRIED assignment is deliberately LEFT ALONE: it is the whole point of
            // this slice, and it survives into the next sequential run.
            //
            // Cancel, join EVERY owned task (including the carried delivery), and only THEN clear the
            // slot — the same order every other ownership transition uses, so nothing is abandoned and
            // no task observes a released slot.
            if (_activeAssignment is { } retained && !retained.State.IsCarried)
            {
                try
                {
                    await DrainAssignmentAsync(retained, cancelFirst: true);
                }
                finally
                {
                    // The slot is cleared on EVERY path, including a deferred cancellation failure, so
                    // a follow-up run can never inherit a finished assignment.
                    ClearActiveAssignment();
                }

                _currentTaskId = null;
                _currentRole = null;
            }

            // DETACH, THEN RELEASE — in that order, on EVERY path, including an early setup failure
            // and even a run that never installed a callback. The release runs even when the
            // detachment itself fails, so a failing detach can never leave the service stuck Running.
            try
            {
                DetachProvisioner(primaryFailure);
            }
            finally
            {
                ReleaseRunGuard();
            }
        }
    }

    /// <summary>
    /// THE RUN BODY — everything between claiming the run guard and releasing it. The lexical
    /// transport ownership (<c>using var ownedChannel</c> / <c>using var stream</c>) lives here, so
    /// both the channel and the stream have been disposed or faulted before
    /// <see cref="RunAsync"/>'s <c>finally</c> detaches the provisioner and releases the guard.
    /// </summary>
    /// <param name="ct">Cancellation token that stops the worker.</param>
    /// <returns>The observed run outcome.</returns>
    private async Task<WorkerRunOutcome> RunCoreAsync(CancellationToken ct)
    {
        // THE DEFENSIVE ENTRY RULE. A retained assignment that is NOT Carried is finished here,
        // BEFORE `ConnectAsync`, the initial Ready or any new assignment: it has nothing left to
        // deliver and must never be inherited by this run. In practice this is a SECOND GUARD only —
        // the exit re-check in <see cref="RunAsync"/> already cleared every non-carried assignment
        // after the previous run's lexical disposal — but it also covers a first-ever run on a service
        // that was handed a non-carried assignment directly (the focused fixtures do exactly that),
        // and it keeps the invariant local to the run that owns the ownership slot.
        //
        // A CARRIED assignment is deliberately left untouched: it is the one thing that must survive
        // this boundary, and the reconnect path (round two) is what adopts and delivers it.
        if (_activeAssignment is { } inherited && !inherited.State.IsCarried)
        {
            try
            {
                await DrainAssignmentAsync(inherited, cancelFirst: true);
            }
            finally
            {
                ClearActiveAssignment();
            }

            _currentTaskId = null;
            _currentRole = null;
        }

        // Prepare the agent runner. This creates NO LLM client: worker containers hold no LLM
        // credentials of their own, so the client is created lazily on the first prompt, after
        // the orchestrator has provisioned credentials.
        _log.Info("Preparing SharpCoder agent engine...");
        await _agentRunner.ConnectAsync(ct);

        // Enable HTTP/2 over plaintext (required for gRPC without TLS in Docker network).
        // A test seam may instead supply the call invoker, so the REAL lifecycle can be driven
        // without a live server; in that case there is no channel to own.
        GrpcChannel? channel = null;
        CallInvoker invoker;
        if (CallInvokerFactory is not null)
        {
            invoker = CallInvokerFactory();
        }
        else
        {
            channel = GrpcChannel.ForAddress(orchestratorUrl, new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true,
                }
            });
            invoker = channel.CreateCallInvoker();
        }

        using var ownedChannel = channel;
        var client = new HiveOrchestrator.HiveOrchestratorClient(invoker);

        // 1. Register
        var registerRequest = new RegisterRequest
        {
            WorkerId = workerId,

            // ADDITIVE NEGOTIATION REQUEST — always asked for explicitly, never derived from the
            // capabilities below, from the orchestrator version, or from any task's model. An old
            // orchestrator simply ignores the field and answers with the default (disabled).
            RequestCompletionReceiptAck = true,
        };
        registerRequest.Capabilities.AddRange(capabilities);

        var registerResponse = await client.RegisterAsync(registerRequest, cancellationToken: ct);

        if (!registerResponse.Accepted)
        {
            // REJECTED: nothing was built and nothing is published, so no partial connection can
            // ever be observed by another operation. The returned outcome is the ONLY thing this
            // branch produces — no stream is opened, no connection is constructed, and the
            // registration RPC's own failure (had there been one) would have propagated instead.
            _log.Error("Registration rejected by orchestrator.");
            return WorkerRunOutcome.RegistrationRejected;
        }

        var assignedId = string.IsNullOrEmpty(registerResponse.AssignedWorkerId)
            ? workerId
            : registerResponse.AssignedWorkerId;

        _log.Info($"Registered as {assignedId} (orchestrator v{registerResponse.OrchestratorVersion})");

        // 2. Open the bidirectional work stream. LEXICAL ownership stays here (the `using`), while
        //    the connection owns CHECKED ACCESS to it — one disposal, never two.
        using var stream = WorkStreamFactory is null
            ? client.WorkStream(cancellationToken: ct)
            : WorkStreamFactory(client, ct);

        // 3. Build the connection COMPLETELY. Registration is accepted, the stream is open, and the
        //    provisioner is associated: a supplied TestProvisioner REPLACES the production one for
        //    BOTH provisioning sites, and otherwise the production provisioner is constructed bound
        //    to this connection's own identity and checked fetch.
        //
        //    Registration happens BEFORE the operator may have completed OAuth sign-in, so the
        //    provisioning fetch is deliberately NOT performed here. It runs immediately before every
        //    first LLM client creation, by which time a token committed after sign-in is visible.
        //
        //    The connection also receives THIS SERVICE's environment provenance — the process-lifetime
        //    object the attempt-construction path supplied — so the production provisioner's operator
        //    snapshot is the PROCESS's, not this attempt's. Nothing else is shared: the connection
        //    still owns its identity, client, stream and provisioner.
        //
        //    THE TWO NEGOTIATED ANSWERS are captured here, each from the ACCEPTED response's own
        //    EXPLICIT field and from nothing else, so they are fixed facts of this connection BEFORE
        //    publication. An absent or false answer leaves its own fact false, and because the facts
        //    live on the connection object, a later sequential run retains nothing from this one.
        //    They are captured, never derived from one another: the ACK answer comes from the Ack
        //    field and the ordinary-readiness requirement from the Ready field, and the gated
        //    ordinary readiness needs BOTH.
        var connection = new WorkerConnection(
            assignedId, client, stream, provisionerOverride: TestProvisioner,
            provisioningEnvironment: _provisioningEnvironment,
            completionReceiptAckEnabled: registerResponse.CompletionReceiptAckEnabled,
            completionReadyRequired: registerResponse.CompletionReadyRequired);

        // 4. PUBLISH — only now that construction has fully succeeded, and BEFORE the initial Ready
        //    and before any assignment processing.
        PublishConnection(connection);

        // From here on EVERY step is covered by cleanup. The connection is observable the moment it
        // is published, so any fallible post-publication setup (config-provisioner installation,
        // linked-CTS creation, heartbeat startup) must not be able to unwind lexically — disposing
        // the stream and channel — while a nominally usable connection is still published.
        CancellationTokenSource? heartbeatCts = null;
        Task? heartbeatTask = null;

        // The body's PRIMARY failure, recorded before the cleanup block runs. It is the outcome
        // that must survive cleanup: a secondary cancellation-cleanup failure is reported (sanitized)
        // beside it rather than allowed to replace it. Recorded by rethrowing unchanged, so the
        // original exception identity and stack trace are preserved for the caller.
        Exception? primaryFailure = null;

        try
        {
            // 5. Install the LAZY provisioning callback. It is the connection's OWN checked entry
            //    point, so the eager per-assignment site and this lazy site share one provisioner
            //    instance AND one retirement contract — including when a TestProvisioner replaced
            //    the connection's provisioner.
            _agentRunner.SetConfigProvisioner(connection.CreateProvisioningCallback());

            // 6. Start heartbeat background task. The launch point is UNCHANGED: with no seam
            //    supplied this is the production loop over its own 30-second PeriodicTimer; a
            //    controlled task (test seam) receives the SAME ACTUAL owned heartbeatCts, and
            //    RunAsync owns cancellation, joining and disposal identically either way.
            heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            heartbeatTask = HeartbeatTaskFactory is null
                ? RunHeartbeatAsync(connection, heartbeatCts.Token)
                : HeartbeatTaskFactory(connection, heartbeatCts);

            // 7. Send WorkerReady
            await SendWorkerReady(connection, ct);

            // 8. Main message loop
            await ProcessMessagesAsync(connection, ct);
        }
        catch (Exception ex)
        {
            // RECORD the primary failure and rethrow it UNCHANGED (original identity and stack
            // trace), so cleanup below can tell a real message-loop/reader failure from a
            // secondary cancellation-cleanup failure.
            primaryFailure = ex;
            throw;
        }
        finally
        {
            // RETIRE then UNPUBLISH the EXPECTED connection — by reference identity — BEFORE its
            // stream and channel are disposed (both happen as this scope unwinds, after this
            // finally). Retiring first means a NEW operation can never start transport on a
            // connection that is being torn down, while an operation that already captured it keeps
            // its own token and outcome. The message loop's own teardown drain has already run, so
            // the draining body's single Ready attempt was permitted.
            connection.Retire();
            UnpublishConnection(connection);

            // Stop and join the heartbeat, then release the linked source. Both are null when the
            // setup step that creates them faulted, so this cleanup is safe for EVERY point at
            // which the body above can leave.
            //
            // A THROWING CANCELLATION CALLBACK IS CAPTURED, not allowed to bypass the rest of the
            // cleanup: the failure is retained VERBATIM and the teardown still awaits the ORIGINAL
            // heartbeatTask and still disposes its source. Only an already-disposed source (a
            // repeated drain) is tolerated silently.
            var cancellationFailure = heartbeatCts is null
                ? null
                : await CaptureCancellationFailureAsync(heartbeatCts);

            // THE JOIN OUTCOME IS CAPTURED TOO. Awaiting the ORIGINAL heartbeat task can itself
            // fault with a non-cancellation error (the heartbeat loop's own diagnostic is guarded,
            // but the seam-supplied task is arbitrary). Letting that fault unwind this `finally`
            // would skip the disposal below AND silently replace both a RunAsync primary and the
            // captured cancellation-callback failure, so it is captured instead. Ordinary
            // cancellation stays tolerated exactly as before.
            var joinFailure = heartbeatTask is null
                ? null
                : await CaptureJoinFailureAsync(heartbeatTask);

            heartbeatCts?.Dispose();

            // ERROR PRECEDENCE — ONE authoritative outcome; every other failure is merely REPORTED
            // through guarded sanitized logging (a logger failure can never replace a real one):
            //   1. a RunAsync/message-loop PRIMARY already in flight wins, and BOTH cleanup
            //      failures are reported beside it;
            //   2. otherwise the deferred cancellation-callback failure wins — the heartbeat-join
            //      failure may never replace or discard it, so it is reported instead;
            //   3. otherwise the heartbeat-join failure propagates.
            // Nothing is raised until the join above and the disposal above have completed, so a
            // throwing callback (or a faulting heartbeat) can never be turned into a fabricated
            // successful teardown and can never skip releasing the source.
            if (primaryFailure is not null)
            {
                ReportIfPresent(cancellationFailure, HeartbeatCancellationFailedMessage);
                ReportIfPresent(joinFailure, HeartbeatJoinFailedMessage);
            }
            else if (cancellationFailure is not null)
            {
                ReportIfPresent(joinFailure, HeartbeatJoinFailedMessage);
                RethrowDeferred(cancellationFailure);
            }
            else
            {
                RethrowDeferred(joinFailure);
            }
        }

        // ACCEPTED-REGISTRATION NORMAL COMPLETION. This statement is lexically AFTER the cleanup
        // block above and still INSIDE the lexical `using` scope, so it is reached only when the
        // message loop returned normally AND the whole teardown — retire, unpublish, heartbeat
        // cancel/join/dispose — completed without raising: every exception that escapes today still
        // escapes, and a deferred cleanup failure rethrown above never reaches here. Because the
        // stream and channel `using` disposals run as this scope unwinds, the value is observable
        // to the caller only once the ENTIRE method, including that transport disposal, has
        // finished. It reports observed normal completion of the accepted connection lifecycle and
        // nothing more.
        return WorkerRunOutcome.WorkStreamEnded;
    }

    /// <summary>The sanitized report message for a failed heartbeat cancellation request.</summary>
    private const string HeartbeatCancellationFailedMessage = "Heartbeat cancellation cleanup failed";

    /// <summary>The sanitized report message for a failed heartbeat JOIN.</summary>
    private const string HeartbeatJoinFailedMessage = "Heartbeat join failed";

    /// <summary>The sanitized report message for a failed assignment cancellation request.</summary>
    private const string TaskCancellationFailedMessage = "Task cancellation cleanup failed";

    /// <summary>
    /// Requests cancellation on <paramref name="source"/> and CAPTURES a failure raised by a
    /// cancellation callback instead of letting it unwind the caller's cleanup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CancellationTokenSource.CancelAsync"/> surfaces a throwing callback as the
    /// callback's OWN exception (wrapped in an <see cref="AggregateException"/> when the runtime
    /// aggregated several). That exception is returned VERBATIM — never unwrapped, never
    /// normalized into a single invented type — so the caller can keep the actual caught evidence
    /// observable after it has joined its work and released its resources.
    /// </para>
    /// <para>
    /// An ALREADY-DISPOSED source returns <c>null</c>: that is the pre-existing tolerance for a
    /// repeated drain, not a callback failure, and there is nothing left to cancel. Success also
    /// returns <c>null</c> — a fabricated cancellation outcome is never manufactured.
    /// </para>
    /// </remarks>
    /// <param name="source">The source to cancel.</param>
    /// <returns>The deferred cancellation failure to propagate, or <c>null</c> when none arose.</returns>
    private static async Task<Exception?> CaptureCancellationFailureAsync(CancellationTokenSource source)
    {
        try
        {
            await source.CancelAsync();
            return null;
        }
        catch (ObjectDisposedException)
        {
            // Already disposed by an earlier drain — nothing to cancel.
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// JOINS <paramref name="task"/> to termination and CAPTURES a non-cancellation failure instead
    /// of letting it unwind the caller's cleanup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The task is awaited WITHOUT a caller token, so the join can never be made vacuous and the
    /// work is never abandoned. Ordinary cancellation is tolerated exactly as before (a cancelled
    /// heartbeat is the normal teardown outcome and returns <c>null</c>); anything else is returned
    /// VERBATIM — never unwrapped or normalized into a single invented type — so the caller can
    /// release its resources first and then apply its precedence policy to the actual evidence.
    /// </para>
    /// <para>
    /// A successful join also returns <c>null</c>: no failure is ever manufactured.
    /// </para>
    /// </remarks>
    /// <param name="task">The ORIGINAL task to join.</param>
    /// <returns>The captured join failure, or <c>null</c> for success or ordinary cancellation.</returns>
    private static async Task<Exception?> CaptureJoinFailureAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (OperationCanceledException)
        {
            // Expected: this is how a cancelled background task unwinds.
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// Reports a non-authoritative failure through the guarded sanitized log. A no-op when there is
    /// nothing to report.
    /// </summary>
    /// <param name="failure">The failure to report, or <c>null</c>.</param>
    /// <param name="message">The static, secret-free message describing the cleanup stage.</param>
    private void ReportIfPresent(Exception? failure, string message)
    {
        if (failure is not null)
            TryLogSanitized(message, failure);
    }

    /// <summary>
    /// Reports a cleanup-related failure in sanitized form, GUARDED so a diagnostic can never itself
    /// skip or replace cleanup. Used when a primary failure already exists: the primary propagates
    /// and this is the non-throwing report beside it.
    /// </summary>
    /// <remarks>
    /// The log write is deliberately guarded: a failing <see cref="Console.Error"/> (a test seam, or
    /// a closed stream) must not be able to replace the real failure or abort the remaining cleanup.
    /// Nothing here inspects the failure's message — <see cref="SafeExceptionLog.Describe"/> renders
    /// type names and status codes only, so a provisioned secret can never reach the log.
    /// </remarks>
    /// <param name="message">The static, secret-free message describing the cleanup stage.</param>
    /// <param name="failure">The failure to classify — never rendered as text.</param>
    private void TryLogSanitized(string message, Exception failure)
    {
        try
        {
            _log.Error($"{message} [{SafeExceptionLog.Describe(failure)}]");
        }
        catch
        {
            // A diagnostic must never mask the authoritative outcome.
        }
    }

    /// <summary>The sanitized report message for a RETRANSMISSION attempt that failed.</summary>
    private const string RetransmissionFailedMessage = "Completion retransmission failed";

    /// <summary>
    /// Reports ONE failed retransmission attempt through the EXISTING guarded sanitized path. It is
    /// the retransmitter's only diagnostic: a successful attempt logs nothing at all, so a long-lived
    /// unacknowledged assignment cannot spam the log, and a failed attempt never propagates — the
    /// original Complete attempt's outcome, the retained result and the assignment's own error
    /// handling are all left exactly as they were.
    /// </summary>
    /// <param name="failure">The attempt's failure to classify — never rendered as text.</param>
    private void ReportRetransmissionFailure(Exception failure)
    {
        if (failure is not null)
            TryLogSanitized(RetransmissionFailedMessage, failure);
    }

    /// <summary>
    /// A single-use claim guaranteeing exactly ONE <c>WorkerReady</c> per assignment.
    /// <para>
    /// Two producers can finish an assignment: the task body itself (normal completion, failure,
    /// or observing cancellation) and the cancel handler. If both emit Ready, the orchestrator
    /// dequeues two tasks. A second assignment arriving while a first is still draining then
    /// blocks the single response-reading loop on the drain await, which is the very loop the
    /// first task needs in order to receive its <c>ToolResponse</c> — a deterministic deadlock.
    /// Single-flight Ready removes the extra dequeue that creates that interleaving.
    /// </para>
    /// </summary>
    private sealed class ReadyClaim
    {
        private int _claimed;

        /// <summary>Returns <c>true</c> for the FIRST caller only; every later caller gets <c>false</c>.</summary>
        public bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;
    }

    /// <summary>
    /// The assignment-local slot holding the ONE terminal <see cref="TaskResult"/> an assignment
    /// produced, separated from the connection-bound reporting of that result.
    /// <para>
    /// SINGLE WRITER: only the assignment body publishes, exactly once, with the EXACT complete
    /// domain result <see cref="TaskExecutor.ExecuteAsync"/> returned — before any payload mapping
    /// and before any transport await. Reporting then merely consumes what is already retained,
    /// so a blocked, failed or cancelled completion write cannot lose, truncate or overwrite it.
    /// A body that throws before producing a result leaves this holder EMPTY: no completion is
    /// ever fabricated, and no synthesized transport-failure result is stored.
    /// </para>
    /// <para>
    /// Publication is via <see cref="Interlocked"/> / <see cref="Volatile"/>, so a reader on any
    /// other thread (the message loop after a drain) observes either <c>null</c> or the fully
    /// constructed, immutable result — never a torn reference. Retention lasts exactly as long as
    /// the owning <see cref="ActiveAssignment"/>: the EXISTING drain-then-clear ownership
    /// transition (replacement, matching cancel, teardown) releases it. Nothing here extends the
    /// assignment's lifetime, adds a queue, or survives the loop's teardown.
    /// </para>
    /// </summary>
    private sealed class TerminalResultHolder
    {
        private TaskResult? _result;

        /// <summary>The retained terminal result, or <c>null</c> while none has been produced.</summary>
        public TaskResult? Result => Volatile.Read(ref _result);

        /// <summary>
        /// Retains the assignment's terminal result. Called EXACTLY once per assignment, straight
        /// after the executor returns. A second publish is a bug in the execution boundary (one
        /// assignment can only produce one terminal result), so it fails fast rather than silently
        /// replacing what was already retained.
        /// </summary>
        public void Publish(TaskResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            if (Interlocked.CompareExchange(ref _result, result, null) is not null)
                throw new InvalidOperationException(
                    "A terminal result is already retained for this assignment — it must be published once.");
        }
    }

    /// <summary>
    /// The ASSIGNMENT-LOCAL completion-receipt tracker: the tiny two-transition latch that says
    /// whether THIS assignment's single <c>Complete</c> attempt made an acknowledgement possible
    /// (ARMED) and whether one has since been accepted (CONFIRMED).
    /// <para>
    /// IT IS NOT A HISTORY. There is exactly one of these per assignment, created before either
    /// owned task starts and released by the EXISTING drain-then-clear ownership transition, so
    /// nothing connection-global, no dictionary and no queue of past receipts exists. It holds no
    /// completion payload: the EXACT <see cref="TaskResult"/> stays in its own
    /// <see cref="TerminalResultHolder"/> and is never replaced, augmented or re-read from here.
    /// </para>
    /// <para>
    /// TWO SEPARATE FACTS. Receipt confirmation says only that the orchestrator acknowledged
    /// durably retaining this task's completion evidence. It is NOT "the local write succeeded":
    /// an ACK that arrives while the Complete write is still pending latches here WITHOUT joining
    /// that write, and a write that later fails or is cancelled keeps its own existing outcome.
    /// </para>
    /// <para>
    /// IT IS ALSO THE GATE'S RECEIPT AUTHORITY. On a connection whose accepted registration carried
    /// BOTH negotiated facts, the assignment's ordinary-readiness predicate reads its ARMED and
    /// CONFIRMED answers — and the local write's own termination is tracked separately by the slot —
    /// so the ordinary Ready is withheld until all three hold. The tracker still records the receipt
    /// in every mode (an ACK-only connection stays tracking-only) and never stores a completion
    /// payload, so the slot reading it cannot observe or alter the retained result.
    /// </para>
    /// <para>
    /// The publication is a single <see cref="Interlocked"/> transition — the reporter arms, the
    /// reader acknowledges — so there is no waiting task, no timer, no service and no generalized
    /// protocol framework behind it. Nothing here ever waits for an acknowledgement: retention
    /// until ACK is NOT a postcondition of this slice, and a delayed ACK that arrives after the
    /// existing ownership clear is simply ignored.
    /// </para>
    /// <para>
    /// IT ALSO CARRIES THE ASSIGNMENT'S ONE RETRANSMITTER. On a both-flags connection the assignment
    /// handler attaches the <see cref="CompletionRetry"/> that re-sends this assignment's frozen
    /// completion while the receipt stays unacknowledged; a legacy, ACK-only or readiness-only
    /// connection attaches none, so this stays exactly what it was. The tracker is the natural
    /// carrier because it is the assignment-local object BOTH the reporter (which freezes and arms)
    /// and the reader (which confirms) already hold. The retransmitter holds no completion payload of
    /// its own — the frozen envelope is its own private state — so the retained
    /// <see cref="TaskResult"/> is still neither stored, replaced nor read here.
    /// </para>
    /// </summary>
    /// <param name="owner">
    /// The connection this assignment arrived on. An acknowledgement delivered on any OTHER
    /// connection (a successor registration, a second sequential loop) can never confirm it.
    /// </param>
    private sealed class CompletionReceiptTracker(WorkerConnection owner)
    {
        /// <summary>No Complete attempt has armed this assignment; no acknowledgement is expected.</summary>
        private const int Unarmed = 0;

        /// <summary>The single Complete attempt for an exact mapped result is about to be written.</summary>
        private const int Armed = 1;

        /// <summary>A matching acknowledgement has been accepted. Terminal.</summary>
        private const int Confirmed = 2;

        private int _state = Unarmed;

        /// <summary>
        /// THE ASSIGNMENT'S ONE RETRANSMITTER, or <c>null</c> for an assignment that has none — the
        /// legacy, ACK-only and readiness-only connections, which produce no retry task, no snapshot,
        /// no wait and no attempt. It is attached ONCE, before either owned task starts, by the
        /// assignment handler that decided the GATED shape, so the reporting flow can freeze the ONE
        /// envelope into it and the reader can reach it through the assignment it still retains.
        /// </summary>
        private CompletionRetry? _retry;

        /// <summary>The connection this assignment — and therefore this receipt — belongs to.</summary>
        public WorkerConnection Owner { get; } = owner;

        /// <summary>The assignment's retransmitter, or <c>null</c> when this assignment has none.</summary>
        public CompletionRetry? Retry => Volatile.Read(ref _retry);

        /// <summary>Whether a Complete attempt has made an acknowledgement possible for this assignment.</summary>
        public bool IsArmed => Volatile.Read(ref _state) != Unarmed;

        /// <summary>Whether a matching acknowledgement has been accepted for this assignment.</summary>
        public bool IsConfirmed => Volatile.Read(ref _state) == Confirmed;

        /// <summary>
        /// ARMS this assignment. Called ONLY on an ENABLED connection, ONLY once the exact produced
        /// result has been mapped, and ONLY immediately before the single existing Complete send.
        /// Idempotent, and never able to undo a confirmation that already landed.
        /// </summary>
        public void Arm() => Interlocked.CompareExchange(ref _state, Armed, Unarmed);

        /// <summary>
        /// ACCEPTS one acknowledgement delivered on <paramref name="deliveringConnection"/>.
        /// Returns <c>true</c> for the FIRST accepted receipt only: a duplicate, or an ACK for an
        /// assignment that was never armed, or one delivered on a different connection, changes
        /// nothing and returns <c>false</c>.
        /// </summary>
        public bool TryConfirm(WorkerConnection deliveringConnection) =>
            ReferenceEquals(Owner, deliveringConnection)
            && Interlocked.CompareExchange(ref _state, Confirmed, Armed) == Armed;

        /// <summary>
        /// ATTACHES the ONE retransmitter this assignment may have. Called at most once, by the
        /// assignment handler, before either owned task starts — while nothing can be armed yet, so
        /// no send and no retry can be in flight. It fails fast on a second attach rather than
        /// silently replacing what is already retained.
        /// </summary>
        /// <param name="retry">The retransmitter built for this assignment.</param>
        public void AttachRetry(CompletionRetry retry)
        {
            ArgumentNullException.ThrowIfNull(retry);

            if (Interlocked.CompareExchange(ref _retry, retry, null) is not null)
                throw new InvalidOperationException(
                    "A retransmitter is already attached to this assignment — it must be attached once.");
        }
    }

    /// <summary>
    /// THE RETRANSMISSION INTERVAL. One frozen, still-UNCONFIRMED completion is re-sent exactly this
    /// long after the previous attempt TERMINATED (a successful write, a failed write and a
    /// cancelled write alike). It is a constant: there is deliberately no backoff, no jitter, no
    /// configuration surface and no finite attempt cap.
    /// </summary>
    private static readonly TimeSpan RetransmissionInterval = TimeSpan.FromSeconds(5);

    /// <summary>The one frozen completion envelope a gated assignment can own: its ORIGINAL connection's
    /// assigned worker identity together with the SINGLE mapped <see cref="TaskComplete"/> payload.</summary>
    /// <remarks>
    /// The payload is the mapping RESULT and is never handed to a writer directly — every send, the
    /// original Complete attempt included, clones it — so no writer can mutate what the next attempt
    /// will re-send, and the retained <see cref="TaskResult"/> is never re-mapped or re-read.
    /// </remarks>
    private sealed class FrozenCompletion(string workerId, TaskComplete payload)
    {
        /// <summary>The assigned worker identity captured with the payload.</summary>
        public string WorkerId { get; } = workerId;

        /// <summary>The mapped completion payload, used ONLY as the source of per-send clones.</summary>
        public TaskComplete Payload { get; } = payload;
    }

    /// <summary>
    /// THE ASSIGNMENT-LOCAL LIVE RETRANSMITTER: ONE privately frozen completion envelope, ONE owned
    /// background task, and the retransmission of that frozen envelope on the ORIGINAL connection
    /// while its durable receipt acknowledgement is still missing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHAT IT IS FOR. An acknowledgement can be lost while the stream is still perfectly alive —
    /// either because the original Complete write failed locally or because the server's receipt
    /// answer never arrived. On a connection whose ACCEPTED registration carried BOTH negotiated
    /// facts, the assignment's ordinary Ready is withheld until a matching receipt is confirmed, so
    /// such a loss would stall the worker forever. Re-sending the SAME completion on the SAME stream
    /// lets the server's existing latest-eligible re-acknowledgement close the gap. This is
    /// deliberately NOT disconnect, restart or reconnect survival: it neither opens nor targets any
    /// other connection, and the frozen envelope never outlives its assignment.
    /// </para>
    /// <para>
    /// GATED ONLY, AND BOUNDED. It exists only for an assignment whose accepted registration carried
    /// both negotiated facts (the assignment handler builds it — and the receipt attaches it — only
    /// in that shape), so a legacy, ACK-only or readiness-only connection has no snapshot, no task,
    /// no wait and no attempt at all. At most one envelope is ever frozen (<see cref="Freeze"/> fails
    /// fast on a second), at most one task is ever started (<see cref="Start"/> fails fast on a
    /// second), and the one task re-uses ONE delay and ONE retransmission at a time — there is no
    /// attempt history, no accumulating continuation, no timer backlog and no catch-up burst.
    /// </para>
    /// <para>
    /// ONE GATE, ONE WRITER. The retransmission acquires the SAME service-wide
    /// <see cref="SemaphoreSlim"/> gate every other write uses, so it can never run concurrently with
    /// the assignment's own Complete or readiness write, and it is awaited to termination with the
    /// assignment's EXISTING stream token and released only afterwards. The retry-lifetime
    /// cancellation stops the pending delay and the permit acquisition, but the admitted write is
    /// awaited with the STREAM token, so no cancellation of this retry can abandon a write that has
    /// already begun.
    /// </para>
    /// <para>
    /// ADMISSION IS DECIDED AFTER THE PERMIT. Waiting for the permit and THEN checking the receipt is
    /// what makes the stop meaningful: the admission decision is taken atomically under this object's
    /// own lock once the permit is already held, so a confirmation that won performs no transport
    /// invocation at all, while a retry admitted first may finish even if the acknowledgement arrives
    /// before its writer invocation. No lock is ever held across an <c>await</c>.
    /// </para>
    /// <para>
    /// IT NEVER REPLACES AN OUTCOME. An attempt that fails is reported through ONE guarded, sanitized
    /// diagnostic and the loop waits another full interval; nothing here re-raises, retries the
    /// original reporting, touches the retained result, the ownership slot, the Ready claim or the
    /// acknowledgement handler's own recording.
    /// </para>
    /// </remarks>
    private sealed class CompletionRetry
    {
        private readonly object _gate = new();
        private readonly WorkerConnection _connection;
        private readonly CancellationToken _streamToken;
        private readonly CompletionReceiptTracker _receipt;
        private readonly TimeProvider _clock;
        private readonly SemaphoreSlim _sendGate;
        private readonly Action<Exception> _reportFailure;

        /// <summary>
        /// THE RETRANSMISSION LIFETIME — this retry's OWN source, deliberately NOT linked to the
        /// assignment's CTS and never handed to a write: cancelling it stops the pending delay and
        /// the permit acquisition only, so an already-admitted transport write (awaited with the
        /// stream token) can never be cancelled by it.
        /// </summary>
        private readonly CancellationTokenSource _lifetime = new();

        private FrozenCompletion? _frozen;
        private int _started;
        private bool _admissionClosed;

        /// <summary>
        /// Creates the retransmitter for ONE assignment. It freezes, starts and writes nothing by
        /// itself: the exact connection, stream token, receipt tracker, clock and the SERVICE'S ONE
        /// send gate are captured here, before either owned task starts.
        /// </summary>
        /// <param name="connection">The ORIGINAL connection the assignment arrived on.</param>
        /// <param name="streamToken">The ORIGINAL stream token every retransmission is awaited with.</param>
        /// <param name="receipt">The assignment-local receipt tracker the admission decision consults.</param>
        /// <param name="clock">The clock snapshotted for this assignment; the retry delay's only consumer.</param>
        /// <param name="sendGate">The service's SINGLE send gate, shared, never a second gate.</param>
        /// <param name="reportFailure">The guarded sanitized reporter for a failed attempt.</param>
        public CompletionRetry(
            WorkerConnection connection,
            CancellationToken streamToken,
            CompletionReceiptTracker receipt,
            TimeProvider clock,
            SemaphoreSlim sendGate,
            Action<Exception> reportFailure)
        {
            _connection = connection;
            _streamToken = streamToken;
            _receipt = receipt;
            _clock = clock;
            _sendGate = sendGate;
            _reportFailure = reportFailure;
        }

        /// <summary>
        /// FREEZES the ONE privately owned envelope for this assignment — the captured assigned
        /// worker identity plus the mapped payload — exactly once. Called by the reporting flow on
        /// the successful-mapping path only and BEFORE the receipt is armed, so an absent result or a
        /// mapping that threw freezes nothing and no retransmission can ever occur.
        /// </summary>
        /// <param name="workerId">The ORIGINAL connection's assigned identity.</param>
        /// <param name="payload">The SINGLE mapped completion payload.</param>
        public void Freeze(string workerId, TaskComplete payload)
        {
            ArgumentException.ThrowIfNullOrEmpty(workerId);
            ArgumentNullException.ThrowIfNull(payload);

            if (Interlocked.CompareExchange(
                    ref _frozen, new FrozenCompletion(workerId, payload), null) is not null)
            {
                throw new InvalidOperationException(
                    "A completion envelope is already frozen for this assignment — it must be frozen once.");
            }
        }

        /// <summary>
        /// Builds the NEXT send from a FRESH deep clone of the frozen snapshot: a new message carrying
        /// a new <see cref="TaskComplete"/> each time, so the private snapshot is never handed to a
        /// writer and never mutated by one. Used by the ORIGINAL Complete attempt as well as by every
        /// retransmission, so both send the identical payload.
        /// </summary>
        /// <exception cref="InvalidOperationException">Nothing has been frozen for this assignment.</exception>
        public WorkerMessage NextMessage()
        {
            var frozen = Volatile.Read(ref _frozen)
                ?? throw new InvalidOperationException(
                    "No completion envelope has been frozen for this assignment.");

            return new WorkerMessage
            {
                WorkerId = frozen.WorkerId,
                Complete = frozen.Payload.Clone(),
            };
        }

        /// <summary>
        /// STARTS the ONE owned retransmission task, which OBSERVES the original reporting task's
        /// termination and then re-sends the frozen completion while it stays armed and unconfirmed.
        /// Called exactly once by the assignment handler, AFTER the reporting task exists; the
        /// returned task is what <see cref="ActiveAssignment.Retry"/> retains and every drain joins.
        /// </summary>
        /// <param name="reporting">The assignment's ORIGINAL reporting task.</param>
        /// <returns>The ONE owned retransmission task.</returns>
        public Task Start(Task reporting)
        {
            ArgumentNullException.ThrowIfNull(reporting);

            if (Interlocked.Exchange(ref _started, 1) != 0)
                throw new InvalidOperationException(
                    "The retransmission task is already started for this assignment — it must be started once.");

            return RunAsync(reporting);
        }

        /// <summary>
        /// CLOSES retry admission for good and cancels the pending delay or permit wait. Called by
        /// every drain BEFORE it joins the task. It is synchronous, awaits nothing, touches no
        /// transport and — because an admitted write is awaited with the stream token — cannot cancel
        /// a write already in flight.
        /// </summary>
        public void CloseAdmission()
        {
            lock (_gate)
            {
                if (_admissionClosed)
                    return;

                _admissionClosed = true;
            }

            CancelPendingWait();
        }

        /// <summary>
        /// ACCEPTS one acknowledgement ON BEHALF of the assignment's
        /// <see cref="CompletionReceiptTracker"/> and, on the FIRST acceptance only, closes future
        /// retry admission — the receipt transition and the admission close happening under this
        /// object's OWN lock, so the retransmission's post-permit arbitration (which reads the same
        /// state under the same lock) can never observe a confirmation without also observing the
        /// closed admission.
        /// </summary>
        /// <remarks>
        /// It never awaits, never joins the retry task and never cancels an admitted transport write:
        /// only the pending delay or permit wait is stopped. A duplicate, a wrong task, a wrong worker,
        /// an unarmed assignment and a delivery on another connection all return <c>false</c> and
        /// leave admission exactly as it was.
        /// </remarks>
        /// <param name="deliveringConnection">The connection the acknowledgement was delivered on.</param>
        /// <returns><c>true</c> for the FIRST accepted receipt only.</returns>
        public bool TryConfirm(WorkerConnection deliveringConnection)
        {
            lock (_gate)
            {
                if (!_receipt.TryConfirm(deliveringConnection))
                    return false;

                _admissionClosed = true;
            }

            CancelPendingWait();
            return true;
        }

        /// <summary>Releases the retry-lifetime source, AFTER the task has been joined.</summary>
        public void Dispose() => _lifetime.Dispose();

        /// <summary>
        /// Cancels the pending delay and the pending permit acquisition by cancelling this retry's OWN
        /// lifetime — never the transport write, which is awaited with the assignment's stream token.
        /// Called OUTSIDE the lock, so no cancellation callback ever runs under it, and fault-contained,
        /// so a failing cancellation request can never escape a handler or a drain.
        /// </summary>
        private void CancelPendingWait()
        {
            try
            {
                _lifetime.Cancel();
            }
            catch
            {
                // A cancellation request must never mask the caller's own outcome.
            }
        }

        /// <summary>
        /// THE ONE RETRANSMISSION TASK: observe reporting termination, then — for as long as a frozen,
        /// armed completion remains unconfirmed — wait one interval and attempt one retransmission,
        /// waiting a FRESH interval after every attempt that terminates. It ends when nothing is
        /// frozen, when the receipt is no longer armed, when admission is closed (a confirmation or a
        /// drain) or when the retry lifetime is cancelled.
        /// </summary>
        /// <param name="reporting">The ORIGINAL reporting task.</param>
        private async Task RunAsync(Task reporting)
        {
            // THE PRODUCER JOIN IS UNCONDITIONAL AND UNCANCELLABLE: awaiting the ORIGINAL task (never
            // through a cancellation-skippable continuation) means a producer that was cancelled
            // before its body started is still observed, and its fault is never re-raised here —
            // reporting keeps its own evidence and its own outcome.
            try
            {
                await reporting;
            }
            catch
            {
                // Observed only: reporting's outcome is reported by the drains that join it.
            }

            while (true)
            {
                if (!RetransmissionWanted())
                    return;

                try
                {
                    // THE CLOCK SEAM'S ONLY USE: the TimeProvider-aware overload, cancelled by the
                    // retry lifetime alone.
                    await Task.Delay(RetransmissionInterval, _clock, _lifetime.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (!RetransmissionWanted())
                    return;

                if (!await TryRetransmitAsync())
                    return;
            }
        }

        /// <summary>
        /// WHETHER another retransmission is still wanted: a completion was frozen, the receipt is
        /// ARMED (the mapped Complete made an acknowledgement possible) and NO matching receipt has
        /// been confirmed yet, admission has not been closed by a confirmation or a drain, and the
        /// ORIGINAL connection is still usable.
        /// </summary>
        private bool RetransmissionWanted()
        {
            lock (_gate)
                return AdmissionHolds_Locked();
        }

        /// <summary>
        /// THE ONE ADMISSION PREDICATE, evaluated under this object's lock from facts that only ever
        /// move in one direction — which is what makes the post-permit arbitration and the
        /// acknowledgement's atomic acceptance agree. A retired connection is NOT a live stream, so a
        /// loss on it is out of this retry's scope: it stops the task quietly instead of manufacturing
        /// a failed-attempt diagnostic against a connection that will never accept a write again.
        /// </summary>
        private bool AdmissionHolds_Locked() =>
            !_admissionClosed
            && !_connection.IsRetired
            && Volatile.Read(ref _frozen) is not null
            && _receipt.IsArmed
            && !_receipt.IsConfirmed;

        /// <summary>
        /// ONE retransmission attempt: acquire the SHARED send permit, arbitrate admission atomically
        /// under this object's lock, and — only when admission won — await the actual transport write
        /// with the ORIGINAL stream token, releasing the permit after it terminates.
        /// </summary>
        /// <returns><c>true</c> to keep retransmitting after a fresh interval; <c>false</c> to stop.</returns>
        private async Task<bool> TryRetransmitAsync()
        {
            try
            {
                // NOT MERELY CHECKED BEFORE THE WAIT: the permit is taken first (cancellable by the
                // retry lifetime, so a closed/drained retry never holds it), because only a decision
                // taken with the permit IN HAND can guarantee that a confirmation which won performs
                // no transport invocation.
                await _sendGate.WaitAsync(_lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            try
            {
                var admitted = false;
                lock (_gate)
                {
                    // ARBITRATION — atomic, and the ONLY decision point. The decision is taken WITH
                    // THE PERMIT IN HAND and under the SAME lock the acknowledgement's confirmation
                    // and admission close use, so a confirmation that won performs no transport
                    // invocation at all, while a retry admitted first keeps its own in-flight write
                    // even if the acknowledgement arrives before that writer invocation.
                    admitted = AdmissionHolds_Locked();
                }

                if (!admitted)
                    return false;

                // THE TRANSPORT: a FRESH clone of the frozen snapshot, the ORIGINAL connection and
                // the ORIGINAL stream token, awaited INSIDE the shared permit. Retirement is checked
                // by the connection's own boundary, exactly as every other write does.
                await _connection.EnsureUsable().Stream.RequestStream.WriteAsync(
                    NextMessage(), _streamToken);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                // ONE guarded, sanitized diagnostic per FAILED attempt (type classification only —
                // never a raw message, which can echo provisioned configuration). The attempt failed,
                // so the loop waits a fresh interval and tries again: a failed write is exactly the
                // loss this retry exists to recover from.
                _reportFailure(ex);
                return true;
            }
            finally
            {
                // RELEASED ONLY AFTER THE WRITE TERMINATED — success, failure or cancellation alike.
                _sendGate.Release();
            }
        }
    }

    /// <summary>
    /// THE ASSIGNMENT'S ONE ATOMIC STATE CELL — the single <c>int</c> every ordinary-Ready claim and
    /// every carry transition is decided by, read and claimed exclusively through
    /// <see cref="Interlocked"/> so exactly one participant can win a transition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FOUR STATES, TWO EXCLUSIVE FAMILIES. <see cref="Open"/> is the only state from which a normal
    /// assignment can move: an ordinary Ready write claims <c>Open → ReadyStarted</c>, and a stream
    /// loss claims <c>Open → Carried</c>. Those two claims are therefore MUTUALLY EXCLUSIVE by the
    /// CAS itself — an assignment that already started its ordinary Ready can never be carried, and
    /// a carried assignment can never start an ordinary Ready on its retired original connection.
    /// <see cref="Delivered"/> is reached only from <see cref="Carried"/> (one carried delivery
    /// finished its Complete and its Ready attempt) and is terminal.
    /// </para>
    /// <para>
    /// ONE-WAY AND MONOTONIC. No transition ever moves backwards and none is ever retried: the cell
    /// holds one <c>int</c>, no queue, no waiter and no timer.
    /// </para>
    /// </remarks>
    private sealed class AssignmentState
    {
        /// <summary>No ordinary Ready has been started and no stream loss has carried this assignment.</summary>
        private const int Open = 0;

        /// <summary>This assignment's ORIGINAL connection claimed the ordinary Ready write.</summary>
        private const int ReadyStarted = 1;

        /// <summary>A stream loss carried this assignment; its result is delivered on an adopted connection.</summary>
        private const int Carried = 2;

        /// <summary>The carried result was delivered on an adopted connection. Terminal.</summary>
        private const int Delivered = 3;

        private int _state = Open;

        /// <summary>The raw retained state, for diagnostics and the retained-state readers.</summary>
        public int Value => Volatile.Read(ref _state);

        /// <summary>Whether a stream loss carried this assignment and no delivery has finished yet.</summary>
        public bool IsCarried => Value == Carried;

        /// <summary>Whether the carried delivery finished (its Complete and Ready attempt are done).</summary>
        public bool IsDelivered => Value == Delivered;

        /// <summary>
        /// THE READY-SETTLEMENT CLAIM — <c>Open → ReadyStarted</c>. Returns <c>true</c> for the ONLY
        /// caller allowed to consume the assignment's shared Ready claim and start an ordinary Ready
        /// write; every later caller gets <c>false</c> and must claim nothing and write nothing.
        /// </summary>
        public bool TryStartReady() =>
            Interlocked.CompareExchange(ref _state, ReadyStarted, Open) == Open;

        /// <summary>
        /// THE CARRY CLAIM — <c>Open → Carried</c>. Returns <c>true</c> for the ONLY caller that may
        /// start the assignment's single carried-delivery task; a <c>false</c> result means
        /// the assignment already started an ordinary Ready (which is NOT carried) or was already
        /// carried/delivered.
        /// </summary>
        public bool TryCarry() =>
            Interlocked.CompareExchange(ref _state, Carried, Open) == Open;

        /// <summary>
        /// THE DELIVERY COMPLETION — <c>Carried → Delivered</c>. Returns <c>true</c> for the first
        /// caller only, so the delivered transition is published exactly once.
        /// </summary>
        public bool TryDeliver() =>
            Interlocked.CompareExchange(ref _state, Delivered, Carried) == Carried;
    }

    /// <summary>
    /// Tracks one assignment's identity, its in-flight EXECUTION, its separately owned
    /// connection-bound REPORTING, its separately owned RETRANSMISSION, its cancellation scope, its
    /// Ready claim, its terminal result and its ordinary-readiness slot.
    /// </summary>
    /// <remarks>
    /// UP TO FOUR OWNED TASKS, ONE OWNER. Execution is the work itself (provisioning, preparation,
    /// the executor and the retention of its result); reporting is the transport work of the
    /// ORIGINAL connection's stream (the Complete write) together with the publication of the
    /// ordinary-Ready eligibility fact; on a both-flags connection a THIRD task retransmits the
    /// frozen completion while its receipt stays unacknowledged; and a CARRIED assignment owns a
    /// FOURTH, <see cref="CarriedDelivery"/> — the continuation that delivers the retained result
    /// and its Ready on an ADOPTED connection. They are separated so a held, failed or cancelled
    /// transport write — and a held retransmission — can never keep the execution task itself
    /// running, and so a retry can never hold reporting. The owner keeps EVERY owned task, and
    /// every ownership transition (replacement, matching cancel, teardown, carried drain) joins ALL
    /// of them — and any readiness write already started from the eligibility — before the CTS is
    /// disposed and the slot is cleared, so nothing is ever abandoned.
    /// </remarks>
    private sealed class ActiveAssignment(
        string taskId,
        Task execution,
        Task reporting,
        Task? retry,
        CancellationTokenSource cts,
        ReadyClaim readyClaim,
        TerminalResultHolder terminalResult,
        CompletionReceiptTracker receipt,
        OrdinaryReadySlot ordinaryReady)
    {
        /// <summary>
        /// The assignment's task ID. A <c>CancelTask</c> is only applied when its
        /// <c>TaskId</c> matches this value, so a LATE cancel for an already-finished task can
        /// never abort the assignment that replaced it, nor consume its Ready claim.
        /// </summary>
        public string TaskId { get; } = taskId;

        /// <summary>
        /// THE ASSIGNMENT'S ROLE, set ONCE by the assignment handler when the fully constructed
        /// owner is installed. The heartbeat's task state is restored from here when a stream loss
        /// carries the assignment while its reporter has already cleared the service's live task
        /// state, so the carried assignment keeps being reported as busy — with its real role —
        /// until it is delivered.
        /// </summary>
        /// <remarks>
        /// An <c>init</c> property rather than a constructor parameter, deliberately: the
        /// constructor's parameter list stays EXACTLY what it was, so every existing construction
        /// site (including the focused fixtures that build an owner reflectively) keeps working
        /// unchanged. An owner built without one carries the empty role, exactly like an assignment
        /// whose role was never resolved.
        /// </remarks>
        public string Role { get; init; } = string.Empty;

        /// <summary>
        /// THE ASSIGNMENT'S ONE ATOMIC STATE CELL — the single <c>int</c> every ordinary-Ready claim
        /// and every carry transition of THIS assignment is decided by.
        /// </summary>
        /// <remarks>
        /// The cell lives with the assignment's ordinary-readiness slot because exactly ONE slot
        /// exists per assignment and it is the slot's own settlement that must apply the
        /// <c>Open → ReadyStarted</c> claim BEFORE consulting the shared Ready claim; the owner
        /// exposes the same cell as its state, so the loop's teardown, the refusal boundary and the
        /// carry transitions all read and claim the ONE cell this assignment has.
        /// </remarks>
        public AssignmentState State => OrdinaryReady.State;

        /// <summary>
        /// The running EXECUTION task: provisioning, config-repo preparation, the executor itself,
        /// the retention of the exact terminal result and the execution-owned seam cleanup. It
        /// performs NO completion and NO Ready write, so it can reach termination while a transport
        /// write is still blocked.
        /// </summary>
        public Task Execution { get; } = execution;

        /// <summary>
        /// The CONNECTION-BOUND REPORTING task. It awaits the ORIGINAL <see cref="Execution"/>,
        /// consumes the already-retained result for the Complete mapping and write, and makes the
        /// assignment's single Ready attempt through the shared claim. A blocked or failing write
        /// holds only THIS task; the retransmission task (which observes this one) is separately
        /// owned, so neither can hold the other.
        /// </summary>
        public Task Reporting { get; } = reporting;

        /// <summary>
        /// THE ASSIGNMENT'S ONE RETRANSMISSION TASK, or <c>null</c> on a connection whose accepted
        /// registration did not carry both negotiated facts. It is a SEPARATELY OWNED task, joined
        /// beside the two original tasks by every ownership transition, so no retry can outlive the
        /// assignment's ownership or write on a later connection.
        /// </summary>
        public Task? Retry { get; } = retry;

        /// <summary>Cancellation source scoped to this assignment.</summary>
        public CancellationTokenSource Cts { get; } = cts;

        /// <summary>The shared single-flight Ready claim for this assignment.</summary>
        public ReadyClaim Ready { get; } = readyClaim;

        /// <summary>
        /// The assignment-local holder carrying the terminal result this assignment produced.
        /// It is created BEFORE the body starts and captured directly by the body's closure, so
        /// the body never has to discover its owner through the ownership slot (which may not be
        /// installed yet when the body first runs). Carried here so the EXISTING drain-then-clear
        /// ownership transition retains the result for exactly the assignment's own lifetime.
        /// </summary>
        public TerminalResultHolder TerminalResult { get; } = terminalResult;

        /// <summary>
        /// The assignment-local completion-receipt tracker. Like the result holder it is created
        /// BEFORE either owned task starts and captured directly by the reporting flow's closure,
        /// so the reporter never has to discover it through the ownership slot; it is carried here
        /// as well so the reader — the only other participant — can reach the SAME object through
        /// this assignment's eventual owner, and so the EXISTING drain-then-clear transition
        /// releases it with the rest of the assignment.
        /// </summary>
        public CompletionReceiptTracker Receipt { get; } = receipt;

        /// <summary>
        /// THE ASSIGNMENT'S ORDINARY-READINESS SLOT — the separately published facts that decide the
        /// OLD ORDINARY-READY POINT was reached (the execution terminated NORMALLY, and in the gated
        /// shape also that the mapped/armed Complete's original local write terminated), plus the ONE
        /// readiness write started from them. Created before either owned task starts and captured
        /// directly by the reporting flow (which publishes the facts) and by the reader (which observes
        /// them once and starts the write), so neither has to discover it through the ownership slot.
        /// </summary>
        public OrdinaryReadySlot OrdinaryReady { get; } = ordinaryReady;

        /// <summary>
        /// THE ASSIGNMENT'S ONE CARRIED-DELIVERY TASK, or <c>null</c> while the assignment has not
        /// been carried. Written EXACTLY ONCE by the carry-CAS winner (the loop's stream-loss
        /// teardown) and stored here so every ownership transition that joins this assignment's
        /// tasks joins it too.
        /// </summary>
        /// <remarks>
        /// ONE reference slot, not a list: the carry transition happens once per assignment, so the
        /// delivery is started at most once and is never restarted — an already-carried assignment
        /// observed by a later teardown simply keeps the task it has.
        /// </remarks>
        public Task? CarriedDelivery
        {
            get { lock (_carriedGate) return _carriedDelivery; }
            set { lock (_carriedGate) _carriedDelivery = value; }
        }

        /// <summary>
        /// WHETHER THIS ASSIGNMENT'S CARRIED READY WRITE WAS INITIATED — a ONE-WAY flag set
        /// IMMEDIATELY BEFORE that write is claimed and sent, exactly like the ordinary readiness
        /// slot's started-write fact.
        /// </summary>
        /// <remarks>
        /// It is the carried counterpart of <c>OrdinaryReady.HasStartedWrite</c>, and it is what
        /// authorizes a successor assignment on an ADOPTED connection: once the carried Ready has
        /// genuinely been initiated, the orchestrator may have dequeued the successor, so refusing it
        /// would strand this worker. It is deliberately NOT write completion — the write may still be
        /// pending, and may yet fail, exactly as an ordinary Ready write may.
        /// </remarks>
        public bool CarriedReadyStarted => Volatile.Read(ref _carriedReadyStarted) != 0;

        private Task? _carriedDelivery;
        private readonly object _carriedGate = new();
        private int _carriedReadyStarted;

        /// <summary>
        /// Publishes the one-way fact that the carried Ready write has been INITIATED (the claim was
        /// won and the write is about to be started). Idempotent and monotonic.
        /// </summary>
        public void MarkCarriedReadyStarted() => Interlocked.Exchange(ref _carriedReadyStarted, 1);
    }

    /// <summary>
    /// THE ASSIGNMENT'S ORDINARY-READINESS SLOT: ONE explicitly published fact — that the
    /// assignment's reporting reached the OLD ORDINARY-READY POINT (the execution terminated
    /// NORMALLY) — together with the ONE readiness write started from that fact.
    /// <para>
    /// WHY IT IS SEPARATE FROM THE RESULT. The fact is published at EXACTLY the condition that
    /// gated the old ordinary Ready attempt, so it is NOT inferred from "a result exists" (a
    /// handled provisioning failure produces NO result yet is still eligible) and NOT inferred from
    /// local send success (a failed or cancelled Complete never withdraws it). The reporting task
    /// publishes it and then terminates; the response loop — the EXISTING owner of the assignment's
    /// lifetime — observes it and starts the write, so a completely QUIET response stream still
    /// wakes and Ready is never stranded behind the report.
    /// </para>
    /// <para>
    /// ONE-SHOT ON BOTH SIDES. The write is started by a single settlement: the FIRST settler wins,
    /// takes the assignment's SHARED single-flight Ready claim and retains the one write; every
    /// later settler just observes what is already retained. The write is therefore never
    /// duplicated and never retried, and <see cref="Arm"/> stops handing out a fresh wait once the
    /// slot is settled, so nothing can spin on an already-observed fact. Its connection and token
    /// are the ORIGINAL assignment's own, so no successor assignment or connection can be targeted.
    /// </para>
    /// <para>
    /// NOT ELIGIBLE MEANS NO WRITE AND NO CLAIM. When the eligibility was never published — a
    /// pre-start execution cancellation, an escaping diagnostic, or a report that never reached the
    /// point — the settlement starts nothing and leaves the shared claim UNCONSUMED, which is
    /// exactly what preserves the cancel handler's fallback single-Ready behavior.
    /// </para>
    /// <para>
    /// TWO SHAPES, ONE SLOT. A slot built WITHOUT a receipt tracker is the UNGATED shape: eligibility
    /// alone is the predicate, exactly as before. A slot built WITH the assignment's
    /// <see cref="CompletionReceiptTracker"/> is the GATED shape, and is supplied by the assignment
    /// handler EXACTLY when this connection's accepted registration carried BOTH negotiated facts
    /// (<see cref="WorkerConnection.CompletionReceiptAckEnabled"/> AND
    /// <see cref="WorkerConnection.CompletionReadyRequired"/>). In that shape the ordinary Ready
    /// additionally waits for the mapped, ARMED Complete whose ORIGINAL local write has TERMINATED,
    /// plus a matching same-connection receipt confirmation — the facts may arrive in ANY order, and
    /// each arrival wakes the parked reader, so the Ready is neither lost nor produced early. The
    /// predicate is monotonic, so the reader that wakes can only ever find it satisfied.
    /// </para>
    /// <para>
    /// NO SECOND GATE. The readiness write goes through the SAME <c>_sendGate</c> as everything else,
    /// so a Ready started while an admitted retransmission is still finishing simply serializes
    /// behind it: the two can never write concurrently and neither needs a gate of its own. This slot
    /// is otherwise untouched by retransmission — it never observes, awaits, joins or retries it, and
    /// no Ready retry or successor buffer is added.
    /// </para>
    /// </summary>
    /// <param name="owner">The connection this assignment arrived on.</param>
    /// <param name="streamToken">The ORIGINAL stream token the readiness write is made with.</param>
    /// <param name="claim">The assignment's shared single-flight Ready claim.</param>
    /// <param name="receipt">
    /// The assignment-local completion-receipt tracker, or <c>null</c> for the UNGATED shape. It is
    /// consulted ONLY for the two receipt facts the tracker is the single authority for (the write
    /// was ARMED, and a matching receipt was CONFIRMED); the completion payload itself is never read
    /// from here, and nothing in this slot ever awaits an acknowledgement or joins any write.
    /// </param>
    private sealed class OrdinaryReadySlot(
        WorkerConnection owner,
        CancellationToken streamToken,
        ReadyClaim claim,
        CompletionReceiptTracker? receipt)
    {
        /// <summary>
        /// THE UNGATED SHAPE, for a slot that must not wait for a receipt. Equivalent to a connection
        /// whose accepted registration did not carry both negotiated facts: eligibility alone is the
        /// predicate, so the ordinary Ready is exactly what it was before the gate existed.
        /// </summary>
        /// <param name="owner">The connection this assignment arrived on.</param>
        /// <param name="streamToken">The ORIGINAL stream token the readiness write is made with.</param>
        /// <param name="claim">The assignment's shared single-flight Ready claim.</param>
        public OrdinaryReadySlot(WorkerConnection owner, CancellationToken streamToken, ReadyClaim claim)
            : this(owner, streamToken, claim, receipt: null)
        {
        }

        /// <summary>
        /// The shared await for a SETTLED slot: nothing is left to observe, and because it never
        /// completes it can never be spun on. One instance is reused, so a settled assignment adds
        /// no waiter and no allocation per loop iteration.
        /// </summary>
        private static readonly Task NoObservation = new TaskCompletionSource().Task;

        /// <summary>The shared await for a slot whose readiness predicate already holds: settle NOW, without waiting.</summary>
        private static readonly Task ReadyObservation = Task.CompletedTask;

        private readonly object _gate = new();
        private bool _eligible;
        private bool _completeWriteTerminated;
        private bool _settled;
        private TaskCompletionSource? _wake;
        private Task? _write;

        /// <summary>The connection this assignment — and therefore this readiness write — belongs to.</summary>
        public WorkerConnection Owner { get; } = owner;

        /// <summary>The ORIGINAL stream token, captured with the assignment.</summary>
        public CancellationToken StreamToken { get; } = streamToken;

        /// <summary>
        /// THE ASSIGNMENT'S ONE ATOMIC STATE — the single <c>int</c> this assignment's lifecycle is
        /// decided by, created WITH the assignment's readiness slot because exactly one slot exists
        /// per assignment and it is that assignment's own cell (the owning
        /// <see cref="ActiveAssignment"/> exposes it as its state).
        /// </summary>
        public AssignmentState State { get; } = new();

        /// <summary>The single retained readiness write, or <c>null</c> while none has been started.</summary>
        public Task? Write
        {
            get { lock (_gate) return _write; }
        }

        /// <summary>
        /// Whether this assignment's authorized ordinary Ready WRITE TASK has already been STARTED
        /// and RETAINED — the ONE authorization fact the negotiated successor boundary consults.
        /// </summary>
        /// <remarks>
        /// It is deliberately NOT local write COMPLETION: the write may still be pending, and it may
        /// yet fail or be cancelled, exactly as it could before this boundary existed — the server is
        /// allowed to receive the Ready and dispatch a successor while that write is still in flight.
        /// It is also NOT "the assignment has eligible facts": eligibility, a terminated Complete or a
        /// confirmed receipt are all facts about the PREDICATE, and none of them means Ready has been
        /// emitted. Only a settlement that consumed the shared claim and started (and retained) the
        /// write makes this <c>true</c>, and it is monotonic, so the drain that follows can never
        /// observe authorization being withdrawn.
        /// </remarks>
        public bool HasStartedWrite => Write is not null;

        /// <summary>Whether the ordinary readiness has already been settled.</summary>
        public bool IsSettled
        {
            get { lock (_gate) return _settled; }
        }

        /// <summary>
        /// THE ONE READINESS PREDICATE, evaluated under this slot's lock from facts that only ever
        /// move in one direction.
        /// </summary>
        /// <remarks>
        /// ELIGIBILITY IS ALWAYS REQUIRED: it is published at exactly the condition that gated the
        /// previous ordinary-Ready attempt. The UNGATED shape stops there. The GATED shape
        /// additionally requires the complete set of facts — a mapped and ARMED Complete, that
        /// Complete's ORIGINAL local write TERMINATED, and a matching receipt confirmation — with no
        /// dependence on the order in which they arrived and no exemption for an assignment that was
        /// never armed (an absent result, a handled provisioning failure or a mapping failure
        /// therefore produces NO ordinary Ready). Because an unarmed assignment can never be
        /// confirmed, the tracked facts can never disagree with the tracker.
        /// </remarks>
        private bool ReadinessHolds_Locked()
        {
            if (!_eligible)
                return false;

            // UNGATED SHAPE: eligibility is the whole predicate, exactly as before this gate.
            if (receipt is null)
                return true;

            // GATED SHAPE: every remaining fact must have arrived. ARMED is what a successfully
            // mapped result produced immediately before the single Complete attempt; TERMINATED is
            // that attempt's own local outcome (success, failure or cancellation alike — an
            // acknowledgement is durable receipt, not local write success); CONFIRMED is the
            // tracker's accepted, same-connection, matching receipt.
            return receipt.IsArmed && _completeWriteTerminated && receipt.IsConfirmed;
        }

        /// <summary>
        /// ARMS the one wait the reader races its pending response read against. The returned task
        /// completes only when the WHOLE readiness predicate holds — and is ALREADY complete when it
        /// held before this call — and becomes a never-completing await once the slot has been
        /// SETTLED, so the reader can neither spin on an already-observed signal nor accumulate
        /// waiters: exactly one wait exists per assignment.
        /// </summary>
        /// <remarks>
        /// AN ELIGIBLE-BUT-NOT-YET-READY ASSIGNMENT PARKS HERE. In the gated shape the eligibility
        /// alone does NOT hand the reader a completed signal, so the reader waits until the remaining
        /// facts arrive and wakes it; only a satisfied predicate makes the wait complete.
        /// </remarks>
        public Task Arm()
        {
            lock (_gate)
            {
                if (_settled)
                    return NoObservation;

                if (ReadinessHolds_Locked())
                    return ReadyObservation;

                _wake ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _wake.Task;
            }
        }

        /// <summary>
        /// PUBLISHES the fact that the old ordinary-Ready point was reached. Idempotent and additive
        /// only: it never consumes the shared Ready claim, never writes, and never waits.
        /// </summary>
        public void PublishEligibility()
        {
            lock (_gate)
            {
                if (_eligible)
                    return;

                _eligible = true;
            }

            WakeIfReady();
        }

        /// <summary>
        /// PUBLISHES the fact that the assignment's ORIGINAL local Complete write has TERMINATED —
        /// success, failure or cancellation alike. Idempotent and additive only: it never consumes
        /// the shared Ready claim, never writes, and never waits, so a write whose outcome is a
        /// failure keeps that outcome and can still authorize the ordinary Ready once the receipt is
        /// confirmed.
        /// </summary>
        public void PublishCompleteWriteTerminated()
        {
            lock (_gate)
            {
                if (_completeWriteTerminated)
                    return;

                _completeWriteTerminated = true;
            }

            WakeIfReady();
        }

        /// <summary>
        /// WAKES a parked observation because the matching receipt has just been CONFIRMED. The
        /// receipt itself is recorded by the assignment's <see cref="CompletionReceiptTracker"/>, so
        /// this only re-evaluates the predicate for whoever is parked on it — it never awaits, never
        /// joins the write, and never touches the retained result or the ownership slot.
        /// </summary>
        public void NoteReceiptConfirmed()
        {
            // UNGATED SHAPE: the receipt was never part of this slot's predicate, so there is
            // genuinely nothing to re-evaluate — the acknowledgement stays tracking-only.
            if (receipt is null)
                return;

            WakeIfReady();
        }

        /// <summary>
        /// Completes the ONE parked observation when the readiness predicate holds. A no-op when
        /// there is nothing parked (a later <see cref="Arm"/> sees the satisfied predicate directly)
        /// or when the slot has already settled: the wait is never handed out twice, and a fact that
        /// arrives after settlement can neither produce a second Ready nor resurrect a settled slot.
        /// </summary>
        private void WakeIfReady()
        {
            TaskCompletionSource? wake;
            lock (_gate)
            {
                if (_settled || !ReadinessHolds_Locked())
                    return;

                wake = _wake;
            }

            wake?.TrySetResult();
        }

        /// <summary>
        /// SETTLES the ordinary readiness EXACTLY ONCE and returns the ONE retained write (or
        /// <c>null</c> when the readiness predicate does not hold, or when the shared claim was
        /// already taken). The caller joins the returned write; nothing here awaits it.
        /// </summary>
        /// <remarks>
        /// A predicate that does not (yet) hold settles NOTHING: the slot stays unsettled and the
        /// shared claim stays UNCONSUMED, so a later arrival of the missing fact can still produce
        /// the assignment's single Ready and a matching cancel still retains its fallback. This is
        /// the SAME gate every drain and ownership transition consults, so no drain can ever emit an
        /// unacknowledged ordinary Ready — and no drain ever waits for an acknowledgement, because a
        /// missing receipt simply settles nothing.
        /// </remarks>
        /// <param name="startWrite">
        /// Starts the single readiness write and returns the ORIGINAL task for it — the very task
        /// every ownership transition joins. It is invoked at most once, INSIDE this slot's lock, so
        /// a concurrent reader can never observe a settled slot whose write is not yet retained. It
        /// must therefore return promptly and must not call back into this slot.
        /// </param>
        public Task? Settle(Func<WorkerConnection, CancellationToken, Task> startWrite)
        {
            lock (_gate)
            {
                if (_settled)
                    return _write;

                // THE PREDICATE, NOT MERELY ELIGIBILITY. A gated assignment whose receipt has not
                // been confirmed yet starts nothing and leaves the shared claim UNCONSUMED, exactly
                // as an ineligible assignment does, so the cancel handler's fallback single Ready is
                // preserved.
                if (!ReadinessHolds_Locked())
                    return null;

                // THE ONE READY-SETTLEMENT RULE, applied by EVERY ordinary-Ready path (the response
                // loop's observation and the settlement inside every drain) BEFORE the claim is
                // consulted: only the Open → ReadyStarted winner may consume the shared claim or
                // start an ordinary Ready write. A LOSER claims nothing and writes nothing — which is
                // what keeps a Carried (or Delivered) assignment from ever emitting an ordinary
                // Ready on its retired original connection, and what keeps ReadyStarted and Carried
                // mutually exclusive.
                //
                // The loser still marks the slot SETTLED — with no write retained — for the same
                // reason a winner does: the state it lost to can never move back to Open, so no Ready
                // can ever be produced from this slot again. Leaving it unsettled would hand the
                // response loop a completed readiness observation on EVERY iteration and spin it.
                if (!State.TryStartReady())
                {
                    _settled = true;
                    return _write;
                }

                _settled = true;

                // Single-flight: the claim is consumed by the write, so at most one Ready is ever
                // produced for this assignment and a failed write is never retried.
                if (!claim.TryClaim())
                    return null;

                _write = startWrite(Owner, StreamToken);
                return _write;
            }
        }
    }

    // ── Assignment ownership slot ───────────────────────────────────────────────
    //
    // The retained assignment for this service's current connection. The message loop
    // (<see cref="ProcessMessagesAsync"/>) is the SOLE transition authority: the slot is
    // installed, drained and cleared only through the ownership helpers below, never
    // from any background caller, heartbeat or bridge method. Clearing always happens
    // AFTER the corresponding <see cref="DrainAssignmentAsync"/> has returned — i.e.
    // after BOTH the assignment's execution and its reporting have been joined — so the
    // slot never reads as empty while an assignment is still unwinding. A completed
    // assignment stays RETAINED — clearing happens on replacement, on a matching cancel,
    // or on the loop's teardown, never on task completion.

    /// <summary>
    /// The retained assignment for this connection: the running (or already-finished) execution
    /// task, its connection-bound reporting task, its assignment-scoped CTS and its single-flight
    /// Ready claim. <c>null</c> only before the first install and after an ownership clear, and
    /// empty again after a successful loop teardown.
    /// </summary>
    private ActiveAssignment? _activeAssignment;

    /// <summary>
    /// Ownership transition — INSTALL. Called after EVERY owned task has been OBTAINED (the execution
    /// from <c>Task.Run</c>, the reporting from its own async invocation, and the retransmission from
    /// its own <c>Start</c> on a both-flags connection), so only fully constructed state (task ID,
    /// the owned tasks, CTS, Ready claim, result holder) is ever published; those tasks capture the
    /// assignment-local values, not this slot.
    /// </summary>
    private void InstallActiveAssignment(ActiveAssignment assignment) => _activeAssignment = assignment;

    /// <summary>
    /// THE FIXED, SECRET-FREE protocol error text for an unexpected successor assignment that arrived
    /// BEFORE the retained predecessor's authorized ordinary Ready write task had been started.
    /// </summary>
    internal const string AssignmentBeforeAuthorizedReadyMessage =
        "Assignment received before authorized Ready.";

    /// <summary>
    /// OWNERSHIP BOUNDARY — THE PRE-READY ASSIGNMENT REFUSAL. Consulted from the message loop BEFORE
    /// any replacement drain, runner reset, successor installation or successor execution, so a
    /// refused assignment provably leaves the predecessor's work, the runner and the ownership slot
    /// exactly as they were.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHERE THE BOUNDARY IS. For a connection whose ACCEPTED registration carried BOTH negotiated
    /// facts, the orchestrator advertised that this registration's NEXT ordinary assignment waits for
    /// an accepted Ready after its negotiated completion. An assignment that arrives while a
    /// predecessor is still retained is therefore legitimate ONLY once this worker has actually
    /// STARTED AND RETAINED that predecessor's authorized ordinary Ready write task — which is the
    /// point at which the server could have received the Ready and dispatched the successor. The
    /// boundary is deliberately NOT local write COMPLETION (the dispatched successor may well run
    /// while the write is still in flight) and NOT merely having eligible facts (eligibility, a
    /// terminated Complete and a confirmed receipt are predicate facts, not a sent Ready).
    /// </para>
    /// <para>
    /// A READER-FIRST TIE IS THE PRE-BOUNDARY CASE. If the loop wins the race and receives the
    /// assignment before the readiness settlement has run, the predecessor has eligible facts but no
    /// started write, so the assignment is refused — merely being eligible is not authorization.
    /// </para>
    /// <para>
    /// NO RETAINED OWNER IS ALWAYS ALLOWED. The initial assignment, and any state after the EXISTING
    /// explicit ownership clear (a matching cancel, or a completed teardown drain), has nothing to
    /// authorize against, so the ordinary assignment path proceeds untouched.
    /// </para>
    /// <para>
    /// LEGACY AND ACK-ONLY CONNECTIONS ARE BYTE-IDENTICAL TO BEFORE. With the gate disabled — the
    /// ACK-only shape, the readiness-requirement-only shape, and a connection that negotiated neither
    /// fact — this returns immediately, so the existing replacement drain and its behavior are
    /// completely unchanged. Nothing is inferred here: no acknowledgement, no completion and no
    /// synthesized result.
    /// </para>
    /// </remarks>
    /// <param name="connection">The connection the assignment arrived on.</param>
    /// <exception cref="InvalidOperationException">
    /// A both-flags connection retained a predecessor whose authorized ordinary Ready write task had
    /// not yet been started. The text is the fixed <see cref="AssignmentBeforeAuthorizedReadyMessage"/>.
    /// </exception>
    private void RefuseAssignmentBeforeAuthorizedReady(WorkerConnection connection)
    {
        // A CARRIED (or DELIVERED) predecessor is decided FIRST, in EVERY negotiated mode and
        // regardless of the ordinary-readiness gate: its authorization is the CARRIED Ready's own
        // one-way started-write fact, so the ordinary rule below must not be consulted for it at all.
        if (_activeAssignment is { } predecessor
            && (predecessor.State.IsCarried || predecessor.State.IsDelivered))
        {
            if (!predecessor.CarriedReadyStarted)
                throw new InvalidOperationException(AssignmentBeforeAuthorizedReadyMessage);

            return;
        }

        // LEGACY / ACK-ONLY: for those shapes the ordinary-Ready boundary does not exist at all, and
        // there is no carried predecessor left to consider.
        if (!OrdinaryReadyGateEnabled(connection))
            return;

        // THE BOUNDARY. A retained predecessor whose ordinary Ready write task was never started has
        // authorized nothing, so the successor is refused BEFORE it can reset the runner, install
        // itself, or begin any work — and no ACK, completion or buffered assignment is invented to
        // paper over the gap.
        if (_activeAssignment is { } retained && !retained.OrdinaryReady.HasStartedWrite)
            throw new InvalidOperationException(AssignmentBeforeAuthorizedReadyMessage);
    }

    /// <summary>
    /// Ownership transition — DRAIN-THEN-CLEAR on REPLACEMENT. Awaits the retained execution and its
    /// reporting (and, in the gated shape, its retransmission) WITHOUT cancelling them — their Ready
    /// already flowed, so they are finished or finishing — disposes the CTS, and only then clears the
    /// slot.
    /// </summary>
    /// <remarks>
    /// The clear happens ONLY after the drain returned — i.e. after every owned task joined and the
    /// CTS disposal was attempted — so a replacement never installs its own assignment, nor resets the
    /// shared runner, while the original execution, its report or its retransmission is still running.
    /// The retransmission is stopped first (admission closed, pending wait cancelled) so its join
    /// needs no acknowledgement and no interval. With <c>cancelFirst: false</c> there is no
    /// cancellation callback to fail, so nothing is deferred here.
    /// <para>
    /// It also joins any already-started ordinary readiness write (the drain's shared settlement
    /// returns the retained task for a slot that was already settled), so a successor's replacement
    /// joins that write BEFORE the runner is reset or the ownership slot is cleared. This is the
    /// post-boundary half of the negotiated successor rule; the pre-boundary half is the refusal
    /// above, and neither path ever waits for an acknowledgement.
    /// </para>
    /// </remarks>
    private async Task DrainRetainedForReplacementAsync()
    {
        var drained = TakeActiveAssignment();
        var deferredCancellationFailure = await DrainAssignmentAsync(drained, cancelFirst: false);
        ClearActiveAssignment();

        RethrowDeferred(deferredCancellationFailure);
    }

    /// <summary>
    /// Ownership transition — MATCHING-CANCEL clear. Requests assignment cancellation FIRST, drains
    /// every owned task, disposes the CTS, and only then clears the slot. Returns the drained
    /// assignment (so the caller can still consult its single-flight Ready claim) together with
    /// any DEFERRED cancellation-cleanup failure for the caller to propagate AFTER its own
    /// cleanup.
    /// </summary>
    /// <remarks>
    /// The slot is cleared here, but the deferred failure is deliberately RETURNED rather than
    /// thrown: the call site still has to clear <c>_currentTaskId</c>/<c>_currentRole</c> (and, on
    /// the cancel path, consult the Ready claim), and a throwing cancellation callback must never
    /// skip that cleanup. Neither value is a new outcome framework — the drained owner and the
    /// verbatim caught evidence are simply handed back.
    /// </remarks>
    private async Task<(ActiveAssignment Drained, Exception? DeferredCancellationFailure)>
        DrainRetainedForMatchingCancelAsync()
    {
        var drained = TakeActiveAssignment();
        var deferredCancellationFailure = await DrainAssignmentAsync(drained, cancelFirst: true);
        ClearActiveAssignment();

        return (drained, deferredCancellationFailure);
    }

    /// <summary>
    /// Ownership transition — TEARDOWN clear, called from the message loop's <c>finally</c>.
    /// Identical to the matching-cancel clear (cancel, drain, dispose, then clear), except that the
    /// returned assignment is not used (teardown emits no Ready of its own) and the deferred
    /// cancellation failure is handled by the caller AFTER its own heartbeat-state cleanup.
    /// </summary>
    /// <remarks>
    /// The ownership slot is cleared here — after every owned task joined and the CTS disposal was
    /// attempted — so a deferred cancellation failure can never leave the slot occupied for a
    /// subsequent loop invocation.
    /// </remarks>
    private async Task<Exception?> DrainRetainedForTeardownAsync()
    {
        var (_, deferredCancellationFailure) = await DrainRetainedForMatchingCancelAsync();
        return deferredCancellationFailure;
    }

    /// <summary>
    /// THE ERROR-PRECEDENCE RULE for a deferred cancellation-cleanup failure, applied only AFTER
    /// the joins and the resource cleanup have completed.
    /// </summary>
    /// <remarks>
    /// With a PRIMARY failure already propagating, that primary is authoritative and the secondary
    /// failure is merely REPORTED in sanitized, guarded form — it never replaces the real failure.
    /// Without a primary, the deferred failure is re-raised with its ORIGINAL evidence (the exact
    /// instance the cancellation produced, including any <see cref="AggregateException"/> wrapper),
    /// so a throwing callback can never become a fabricated successful teardown. A <c>null</c>
    /// deferred failure and a <c>null</c> primary are both no-ops.
    /// </remarks>
    /// <param name="deferredCancellationFailure">The captured cancellation-cleanup failure, or <c>null</c>.</param>
    /// <param name="primaryFailure">The already-propagating primary failure, or <c>null</c>.</param>
    /// <param name="reportMessage">The static, secret-free message used when reporting a secondary failure.</param>
    private void PropagateOrReport(
        Exception? deferredCancellationFailure, Exception? primaryFailure, string reportMessage)
    {
        if (deferredCancellationFailure is null)
            return;

        if (primaryFailure is not null)
        {
            TryLogSanitized(reportMessage, deferredCancellationFailure);
            return;
        }

        ExceptionDispatchInfo.Capture(deferredCancellationFailure).Throw();
    }

    /// <summary>
    /// Re-raises a DEFERRED cancellation-cleanup failure with its ORIGINAL evidence — the exact
    /// instance <see cref="CancellationTokenSource.CancelAsync"/> produced, including an
    /// <see cref="AggregateException"/> wrapper — so the caller observes the same failure the
    /// callback raised rather than a normalized or synthesized substitute. A no-op when there is no
    /// deferred failure.
    /// </summary>
    /// <param name="deferredCancellationFailure">The captured failure, or <c>null</c>.</param>
    private static void RethrowDeferred(Exception? deferredCancellationFailure)
    {
        if (deferredCancellationFailure is not null)
            ExceptionDispatchInfo.Capture(deferredCancellationFailure).Throw();
    }

    /// <summary>
    /// Removes and returns the retained assignment. Failing fast on an empty slot keeps the
    /// transition authority honest: an empty slot means there is nothing to drain, and any
    /// caller that thought otherwise is a bug.
    /// </summary>
    private ActiveAssignment TakeActiveAssignment() =>
        _activeAssignment
        ?? throw new InvalidOperationException(
            "No retained assignment to drain — the ownership slot is already empty.");

    /// <summary>Clears the ownership slot. Called only AFTER the drain has returned.</summary>
    private void ClearActiveAssignment() => _activeAssignment = null;

    /// <summary>
    /// The message loop for ONE published connection. The connection is SNAPSHOTTED by the caller
    /// and passed in explicitly (never re-read from the service field), so every Ready, drain and
    /// completion this loop produces belongs to the registration it was started for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// RETIREMENT ORDER. The loop's <c>finally</c> ends this connection's tool-response waits FIRST,
    /// then drains the retained assignment, and only THEN retires the connection. Ending responses
    /// before the drain matters: a bridge call parked on a response whose loop has ended can never
    /// be released by a response, so a wait bound to an independent live token would otherwise hold
    /// the drain — and retirement — off forever. Draining before retiring means an EOF or reader
    /// failure with a LIVE token still permits the draining body's single Ready attempt (that Ready
    /// is written while the connection is still usable). Retiring before the caller disposes the
    /// stream means a new operation can never start transport on a connection whose stream is about
    /// to go away.
    /// </para>
    /// <para>
    /// TWO WAITS, ONE READER. The loop owns exactly ONE pending response read and, beside it, the
    /// retained assignment's ordinary-readiness observation. A COMPLETELY QUIET response stream
    /// therefore still wakes the loop when an assignment's report terminates and, in the negotiated
    /// mode, its receipt is confirmed: the readiness predicate is observed ONCE (the slot stops
    /// handing out an awaitable signal as soon as it is settled, so the loop can never spin on an
    /// already-observed fact) and the ACTUAL Ready write is STARTED and RETAINED — never awaited here
    /// — so an outstanding Ready write cannot block ToolResponse or receipt-ACK consumption. There is
    /// no second reader, no dispatch queue, no polling and no detached continuation: the loop remains
    /// the ONLY ownership transition authority.
    /// </para>
    /// <para>
    /// BOTH DRAIN PATHS CONSULT THE SAME GATE. A held WriteAsync — and, in the negotiated mode, a
    /// withheld ordinary Ready — means the predicate does not hold, so neither an EOF nor any
    /// ownership transition can emit an unacknowledged Ready. Neither drain ever WAITS for an
    /// acknowledgement: the gates are pure local facts, and a missing receipt simply settles nothing.
    /// </para>
    /// <para>
    /// THE NEGOTIATED SUCCESSOR BOUNDARY. On a both-flags connection, a successor assignment that
    /// arrives while a predecessor is still retained is accepted ONLY once that predecessor's
    /// authorized ordinary Ready write task has been started and retained; before that the assignment
    /// is refused with the fixed protocol error, and after it the existing replacement drain joins
    /// the started write before the runner reset and the ownership clear. Legacy and ACK-only
    /// connections keep their existing replacement behavior unchanged.
    /// </para>
    /// <para>
    /// THE RETRANSMISSION BOUNDARY. On a both-flags connection the assignment also owns a
    /// retransmission task, which re-sends the frozen completion while its receipt stays
    /// unacknowledged. It is started with the assignment, retained beside the two original tasks, and
    /// joined by every ownership transition — so a successor's replacement drain closes its admission
    /// and cancels its pending wait BEFORE joining it, the runner reset and the ownership clear
    /// happen only afterwards, and no retry can outlive the assignment or write on a successor's
    /// connection. The retry shares the ONE send gate, so an ordinary Ready started while an admitted
    /// retransmission is finishing simply serializes behind it. No other mode has a retry at all.
    /// </para>
    /// </remarks>
    private async Task ProcessMessagesAsync(WorkerConnection connection, CancellationToken ct)
    {
        var stream = connection.Stream;

        // The loop's PRIMARY failure (a reader fault, a cancelled read, a handler failure). It is
        // recorded before the cleanup block, so a secondary cancellation-cleanup failure reported by
        // the teardown drain can never REPLACE an already-propagating primary error.
        Exception? primaryFailure = null;
        Task<OrchestratorMessage?>? pendingRead = null;

        // THE STREAM-LOSS FLAG. It is set ONLY at the loop's read-await site, and ONLY for the two
        // narrow observations the carried contract recognizes — the pending read reaching EOF, or the
        // pending read itself faulting with an RpcException or an IOException — and only while the
        // PROCESS token is not cancelled. A HANDLER exception (a refused successor, an unknown role,
        // any dispatch failure) and anything observed after process-token cancellation are NOT stream
        // loss: they keep today's cancel-and-drain teardown exactly.
        var streamLoss = false;

        try
        {
            // ONE OWNED PENDING READ, RE-ARMED after each dispatched message (exactly what the
            // previous `await foreach` did), so a handler failure can never leave an unobserved read
            // behind and at most one read is ever outstanding. A reader fault or cancellation
            // surfaces through THIS SAME task.
            pendingRead = ReadNextMessageAsync(stream.ResponseStream, ct);

            while (true)
            {
                var readinessWait = OrdinaryReadinessWait();

                // The READ is checked first, so a completed read is always dispatched ahead of a
                // simultaneously completed readiness signal. A readiness signal that loses this race
                // stays available for the next iteration, because it is only SETTLED below.
                var completed = await Task.WhenAny(pendingRead, readinessWait);
                if (ReferenceEquals(completed, readinessWait))
                {
                    // REPORT TERMINATION, observed even on a completely QUIET stream. The readiness
                    // predicate is settled exactly ONCE — when it holds — and the ACTUAL Ready write
                    // started from it is RETAINED by the assignment — joined by every ownership
                    // transition, NEVER awaited here — so an outstanding Ready write cannot hold off
                    // ToolResponse or receipt-ACK consumption. The SAME gate is consulted by every
                    // drain and ownership transition, so an EOF can never emit an unacknowledged
                    // ordinary Ready, and a predicate that does not hold settles nothing and waits for
                    // nothing.
                    if (_activeAssignment is { } reported)
                        _ = SettleOrdinaryReady(reported.OrdinaryReady);

                    continue;
                }

                OrchestratorMessage? message;
                try
                {
                    // THE READ-AWAIT SITE — the ONLY place a stream loss is ever RECORDED. The
                    // filter is deliberately narrow: a pending read that faulted with an RpcException
                    // or an IOException while the process token is still live is the stream going
                    // away underneath this worker. Every other failure — a cancelled read, a handler
                    // fault, anything at all after process-token cancellation — falls through
                    // unchanged and keeps today's teardown.
                    message = await pendingRead;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && (ex is RpcException or IOException))
                {
                    // OLD-STREAM WRITE BOUNDARY — RETIRE FIRST. This is the VERY FIRST action of the
                    // stream-loss path, taken at the read-await site itself: from here on, every NEW
                    // operation on this connection fails with the existing disconnected error, so a
                    // write that has not yet passed SendAsync's post-gate EnsureUsable check writes
                    // NOTHING to the dead stream. A write that already passed that check is a
                    // tolerated pre-loss write. Retirement is idempotent, and the loop's `finally`
                    // still retires too.
                    streamLoss = true;
                    connection.Retire();
                    ClearAdoption(connection);

                    // The fault keeps propagating exactly as today: it stays the loop's PRIMARY
                    // failure (so a deferred cancellation-cleanup failure can never replace it), and
                    // the caller still observes the original exception identity.
                    throw;
                }

                if (message is null)
                {
                    // EOF — the same stream loss, with no fault at all. RETIRE FIRST, exactly as
                    // above, BEFORE the teardown decides what to do with the retained assignment.
                    if (!ct.IsCancellationRequested)
                    {
                        streamLoss = true;
                        connection.Retire();
                        ClearAdoption(connection);
                    }

                    break;
                }

                switch (message.PayloadCase)
                {
                    case OrchestratorMessage.PayloadOneofCase.Assignment:
                        // THE PRE-READY BOUNDARY, CHECKED FIRST — before the replacement drain, the
                        // runner reset, the CTS/claim construction and the successor installation. On
                        // a connection whose accepted registration carried BOTH negotiated facts, an
                        // assignment that arrives while a predecessor is retained is legitimate ONLY
                        // once that predecessor's authorized ordinary Ready write task has actually
                        // been STARTED and RETAINED (the point at which the server could have received
                        // the Ready and dispatched this successor). Otherwise this throws the FIXED
                        // protocol error and NOTHING has happened yet: no runner reset, no successor
                        // installed or executed, no ACK inferred and no work buffered. Legacy and
                        // ACK-only connections return immediately, so their replacement behavior is
                        // byte-identical to before.
                        RefuseAssignmentBeforeAuthorizedReady(connection);

                        // Task-assignment ownership is serialized: only one task may ever own the
                        // mutable runner and its LLM client, so the previous one is drained BEFORE
                        // the runner is reset. Single-flight Ready (above) ensures the orchestrator
                        // never has two assignments in flight against this worker at once, so this
                        // await cannot starve a previous task of its ToolResponse.
                        if (_activeAssignment is not null)
                        {
                            // Await the retained assignment's owned tasks WITHOUT cancelling:
                            // single-flight Ready means a new assignment only follows a Ready this
                            // assignment already emitted, so its execution, its report and (in the
                            // gated shape) its retransmission are finished or finishing. Cancelling
                            // here would abort work that the orchestrator still expects to complete.
                            // On a both-flags connection this drain is reached only AFTER the
                            // boundary above, so it also joins the already-started ordinary Ready
                            // write before the runner reset and the ownership clear.
                            await DrainRetainedForReplacementAsync();
                        }

                        var assignment = message.Assignment;
                        var domainTask = GrpcMapper.ToDomain(assignment);
                        _log.Info($"Received task {domainTask.TaskId}: {domainTask.GoalDescription}");

                        // Mark busy before async execution so heartbeats reflect the real state
                        _currentTaskId = domainTask.TaskId;
                        _currentRole = domainTask.Role.ToRoleName();

                        // Reset Copilot session with per-task model (if specified by orchestrator)
                        var taskModel = string.IsNullOrEmpty(domainTask.Model) ? null : domainTask.Model;
                        _log.Info($"Task model from orchestrator: '{domainTask.Model}' → resolved: '{taskModel ?? "(SDK default)"}'");
                        await _agentRunner.ResetSessionAsync(taskModel, domainTask.ReasoningEffort, ct);

                        var taskCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                        // The Ready claim, the CTS and the terminal-result holder are created
                        // BEFORE either task starts, so neither ever observes a half-initialised
                        // assignment. (Capturing a variable assigned after Task.Run would race
                        // with the body's first statement — and so would reading the ownership
                        // slot, which is only installed once both tasks have been obtained.)
                        var readyClaim = new ReadyClaim();
                        var bodyCts = taskCts;
                        var terminalResult = new TerminalResultHolder();

                        // THE ASSIGNMENT-LOCAL RECEIPT TRACKER, bound to THIS connection and created
                        // alongside the result holder — before either owned task starts, so it is
                        // reachable by the reporting flow (through its closure), by the readiness slot
                        // below and by its eventual owner (installed below) before any execution or
                        // reporting can run.
                        var receipt = new CompletionReceiptTracker(connection);

                        // THE ASSIGNMENT-LOCAL ORDINARY-READINESS SLOT, created alongside the claim
                        // and bound to THIS connection and THIS stream token — before either task
                        // starts, so the reporting flow publishes into it and the loop observes it
                        // without either ever discovering it through the ownership slot.
                        //
                        // THE GATED SHAPE IS CHOSEN ONCE, HERE, from the two IMMUTABLE negotiated
                        // facts of the ACCEPTED registration this assignment arrived on: only when
                        // BOTH are true is the tracker handed in, so the ordinary Ready then waits for
                        // the mapped/armed Complete whose original local write terminated plus a
                        // matching receipt. Every other combination builds the UNGATED slot and keeps
                        // today's behavior exactly — the initial Ready is sent by SendWorkerReady
                        // before this loop and is ungated in every mode.
                        var gated = OrdinaryReadyGateEnabled(connection);
                        var ordinaryReady = gated
                            ? new OrdinaryReadySlot(connection, ct, readyClaim, receipt)
                            : new OrdinaryReadySlot(connection, ct, readyClaim);

                        // THE ASSIGNMENT-LOCAL RETRANSMITTER — built ONLY in the GATED shape (the
                        // accepted registration that carried BOTH negotiated facts), beside the
                        // tracker that carries it and BEFORE either owned task starts, so the
                        // reporting flow can freeze the one envelope into it and every ownership
                        // transition can close and join it. Legacy, ACK-only and readiness-only
                        // connections get NONE of it: no snapshot, no task, no wait and no attempt is
                        // ever produced for them. Its CLOCK is snapshotted from the service's seam
                        // HERE, once, so a later change of that seam cannot retarget an assignment
                        // that is already running; the ONE shared send gate is passed in rather than
                        // a second gate being created.
                        var retry = gated
                            ? new CompletionRetry(
                                connection, ct, receipt, TimeProvider, _sendGate,
                                ReportRetransmissionFailure)
                            : null;
                        if (retry is not null)
                            receipt.AttachRetry(retry);

                        // THE CONNECTION-BOUND DEPENDENCY PAIR for this assignment. Built from the
                        // assignment's EXPECTED connection BEFORE either task starts, and the SAME
                        // instance is handed to BOTH the bridge slot and the session-client slot of
                        // BOTH executor construction branches below. The executor therefore never
                        // receives this service: a bridge or session call it makes resolves the
                        // assignment's OWN connection (or fails with the existing disconnected error
                        // once that connection retires) rather than whatever is published later.
                        var connectionBound = new ConnectionBoundDependencies(this, connection);

                        // Run task execution concurrently so message loop can process
                        // ToolCallResponse messages from the orchestrator during execution.
                        //
                        // THE EXECUTION TASK IS THE WORK ONLY: provisioning, config-repo
                        // preparation, the executor itself, the retention of the exact terminal
                        // result and the execution-owned seam cleanup. It performs NO Complete and
                        // NO Ready write, so a held, failed or cancelled transport write can no
                        // longer keep the execution itself running.
                        //
                        // The body captures the EXPECTED CONNECTION OBJECT — never independently
                        // mutable stream / client / identity values — so its provisioning and its
                        // session RPCs belong to this registration.
                        var execution = Task.Run(async () =>
                        {
                            try
                            {
                                // STEP 1 — provisioner selection. A connection with NO provisioner
                                // (a direct-loop fixture) SKIPS the whole config-repo preparation
                                // and keeps the LEGACY executor.
                                var provisioner = connection.Provisioner;
                                if (provisioner is null)
                                {
                                    var legacyExecutor = new TaskExecutor(
                                        _agentRunner, connectionBound, sessionClient: connectionBound,
                                        configRepoDir: _configRepoDir);
                                    await ExecuteAssignmentAsync(
                                        legacyExecutor, domainTask, terminalResult, bodyCts.Token);
                                }
                                else
                                {
                                    // STEP 2 — the ONE eager provisioning call, through the
                                    // CONNECTION's checked entry point (the same one the lazy
                                    // runner callback uses), so a retired connection fails
                                    // disconnected instead of starting the provisioner. Everything
                                    // below depends on the provisioner's config-repo accessors,
                                    // which throw until the environment snapshot has been taken.
                                    await connection.EnsureProvisionedAsync(taskModel, bodyCts.Token);

                                    // STEPS 3-4 — the askpass helper and the seam that owns it.
                                    // The seam is a per-assignment LOCAL: WorkerService owns it,
                                    // and this `using` encloses the executor's whole lifetime.
                                    // Its disposal is EXECUTION-owned cleanup, so it completes
                                    // with the execution rather than waiting on any transport.
                                    using var seam = CreateConfigRepoSeam(provisioner);

                                    // STEP 5 — probe / clone / agents directory, BEFORE the
                                    // executor exists, let alone runs.
                                    await PrepareConfigRepoAsync(seam, bodyCts.Token);

                                    // STEP 6 — the executor is constructed LAST and receives the
                                    // caller-owned seam; it never disposes it.
                                    var executor = new TaskExecutor(
                                        _agentRunner, connectionBound, gitOperations: null,
                                        sessionClient: connectionBound, configRepoDir: _configRepoDir,
                                        configRepoSeam: seam);
                                    await ExecuteAssignmentAsync(
                                        executor, domainTask, terminalResult, bodyCts.Token);
                                }
                            }
                            catch (OperationCanceledException) { }
                            catch (Exception ex)
                            {
                                // Sanitized: task execution wraps the LLM HTTP boundary, whose error
                                // payloads can echo provisioned configuration (tokens, API keys).
                                _log.Error($"Task execution failed [{SafeExceptionLog.Describe(ex)}]");
                            }
                        }, ct);

                        // THE CONNECTION-BOUND REPORTING TASK, started from the ORIGINAL execution
                        // task and the assignment-local values this handler already holds — it
                        // never discovers its inputs through the ownership slot (which is only
                        // installed below) and never re-reads the published connection. No extra
                        // Task.Run is needed: the async method's own state machine is the task, and
                        // it OBSERVES a producer that was cancelled before its body ever started
                        // (a cancellation-skippable continuation would silently skip it instead).
                        var reporting = ReportAssignmentAsync(
                            execution, domainTask, connection, terminalResult, receipt, ordinaryReady);

                        // THE ONE RETRANSMISSION TASK — started from the ORIGINAL reporting task and
                        // ONLY in the gated shape (a non-gated assignment has no retransmitter at
                        // all, so this is <c>null</c>: no task, no wait, no attempt). It observes
                        // reporting's termination and then re-sends the frozen completion while it
                        // stays unconfirmed, on THIS connection and THIS stream token. It is a
                        // SEPARATELY OWNED task, so a held or failing retransmission can never hold
                        // reporting itself.
                        var retryTask = receipt.Retry?.Start(reporting);

                        // EVERY OWNED TASK is obtained BEFORE the fully constructed owner is
                        // published, so the slot never exposes a half-built assignment. The ROLE is
                        // carried with the owner so a stream loss can restore the heartbeat's task
                        // state from the assignment itself, without re-deriving it from anything.
                        InstallActiveAssignment(
                            new ActiveAssignment(
                                domainTask.TaskId, execution, reporting, retryTask, taskCts, readyClaim,
                                terminalResult, receipt, ordinaryReady)
                            {
                                Role = domainTask.Role.ToRoleName(),
                            });
                        break;

                    case OrchestratorMessage.PayloadOneofCase.Cancel:
                        var cancel = message.Cancel;
                        _log.Info($"Cancel requested for task {cancel.TaskId}: {cancel.Reason}");

                        if (_activeAssignment is not null)
                        {
                            // Correlate by task ID. A LATE cancel for an already-completed task A
                            // must NOT abort the assignment B that replaced it, and must not
                            // consume B's single-flight Ready claim — doing so would strand B and
                            // desynchronise the orchestrator's view of this worker.
                            if (!string.Equals(_activeAssignment.TaskId, cancel.TaskId, StringComparison.Ordinal))
                            {
                                _log.Info(
                                    $"Ignoring stale cancel for task {cancel.TaskId} — the active task is " +
                                    $"{_activeAssignment.TaskId}, which keeps running.");
                                break;
                            }

                            var (cancelled, cancelDrainFailure) = await DrainRetainedForMatchingCancelAsync();

                            _currentTaskId = null;
                            _currentRole = null;

                            // Single-flight: the drained body normally claims Ready itself. Only
                            // emit here if it did not (e.g. it was cancelled before reaching the
                            // claim), so a cancel never produces a second dequeue.
                            //
                            // THE WRITE'S OUTCOME IS CAPTURED, not allowed to jump past the
                            // deferred failure below: a Ready write that fails or is cancelled
                            // would otherwise unwind straight to the loop's catch and SILENTLY
                            // DISCARD the captured cancellation-callback evidence.
                            Exception? readyFailure = null;
                            if (cancelled.Ready.TryClaim())
                            {
                                try
                                {
                                    await SendWorkerReady(connection, ct);
                                }
                                catch (Exception ex)
                                {
                                    readyFailure = ex;
                                }
                            }

                            // ERROR PRECEDENCE. The ownership clear, the heartbeat-state cleanup and
                            // this cancel handler's single Ready have all completed by now.
                            // A FAILED Ready write is a genuine PRIOR PRIMARY (real transport or
                            // caller cancellation), so it keeps its own identity and propagates,
                            // while the deferred cancellation-callback failure is reported through
                            // the guarded sanitized log rather than being discarded. With a
                            // successful (or unclaimed) Ready the deferred failure propagates.
                            PropagateOrReport(cancelDrainFailure, readyFailure, TaskCancellationFailedMessage);
                            RethrowDeferred(readyFailure);
                        }
                        else
                        {
                            // Nothing in flight — the worker is already idle, so a single Ready
                            // keeps the orchestrator's view accurate.
                            _currentTaskId = null;
                            _currentRole = null;
                            await SendWorkerReady(connection, ct);
                        }
                        break;

                    case OrchestratorMessage.PayloadOneofCase.UpdateAgents:
                        var update = message.UpdateAgents;
                        _log.Info($"Updating custom agent for role: {update.Role}");
                        var parsedRole = WorkerRoleExtensions.ParseRole(update.Role)
                            ?? throw new InvalidOperationException($"Unknown role in UpdateAgents: '{update.Role}'");
                        _agentRunner.SetCustomAgent(parsedRole, update.AgentsMdContent);
                        break;

                    case OrchestratorMessage.PayloadOneofCase.ToolResponse:
                        var response = message.ToolResponse;
                        // Dispatched through the CONNECTION this loop was started for — the SAME
                        // object the request was written on — never a service-global map. A response
                        // whose wait belongs to another (or no) connection is simply untracked here,
                        // which is expected for fire-and-forget tools like report_progress.
                        if (!connection.TryCompleteToolResponse(response))
                            _log.Debug($"Received ToolCallResponse for untracked request: {response.RequestId}");
                        break;

                    case OrchestratorMessage.PayloadOneofCase.CompletionReceiptAck:
                        HandleCompletionReceiptAck(connection, message.CompletionReceiptAck);
                        break;

                    case OrchestratorMessage.PayloadOneofCase.None:
                        break;
                }

                // RE-ARM exactly one pending read for the next iteration. Doing it here (after the
                // handler returned) keeps the OWNED read set at one and means a handler that threw
                // left its read in `pendingRead`, where the catch below can still observe it.
                pendingRead = ReadNextMessageAsync(stream.ResponseStream, ct);
            }
        }
        catch (Exception ex)
        {
            // RECORD the loop's primary failure (reader fault, cancelled read, handler failure) and
            // rethrow it UNCHANGED, so cleanup below can preserve it ahead of a secondary
            // cancellation-cleanup failure.
            primaryFailure = ex;
            throw;
        }
        finally
        {
            // END RESPONSE WAITS FIRST — BEFORE the assignment cancellation/drain below. A tool call
            // parked on a response whose loop has ended can never be released by a response, and a
            // wait bound to an INDEPENDENT live token (not the assignment's) would otherwise hold the
            // drain's await forever. Ending the response lifetime here faults every unresolved wait
            // with the EXISTING disconnected error, so such a caller unwinds and retirement is
            // always reached. A wait that already terminated is unaffected (first-terminal-winner).
            connection.EndToolResponses();

            // Stream shutdown must not leave a task running: Program disposes the runner right
            // after this returns, and a still-running turn holds the client lifecycle lease.
            // Cancel then drain BOTH the execution and its reporting, so the runner is quiescent
            // before disposal. The ownership slot must be empty after successful loop cleanup.
            //
            // A deferred cancellation-cleanup failure is held until AFTER the ownership clear, the
            // heartbeat-state cleanup and retirement below, so a throwing cancellation callback can
            // never skip any of them (nor either of the two joins).
            Exception? teardownDrainFailure;
            var retained = _activeAssignment;

            // THE CARRY CLAIM IS ATTEMPTED FIRST — before this branch decides anything else — because
            // `Open → Carried` is exactly the decision that makes the rest of the teardown differ.
            // It is consulted ONLY on a recorded stream loss: every other loop failure (a handler
            // exception, anything after process-token cancellation) keeps today's teardown exactly.
            // A failed claim leaves the state at ReadyStarted (which is NOT carried), Carried or
            // Delivered, and each of those is handled below.
            var carried = streamLoss && retained is not null && retained.State.TryCarry();

            if (carried && retained is not null)
            {
                // ── STREAM LOSS: THE ASSIGNMENT IS CARRIED, NOT KILLED ───────────────────────────────
                //
                // `Open → Carried` WON, so this assignment never started an ordinary Ready and its
                // single carry transition happens here. NO cancel and NO drain: the execution, the
                // reporting (possibly still blocked in, or still failing, its Complete write on the
                // retired original connection) and the retransmission are deliberately left running —
                // the carried delivery OBSERVES the reporting to termination instead of joining it
                // here, because a pending old-connection write is released by the transport disposal
                // that happens at the end of this run, AFTER this loop has returned.
                //
                // The assignment STAYS RETAINED, so the result it holds (or will hold) survives into
                // the next run, where it is delivered on an ADOPTED connection.
                //
                // The retransmission admission is CLOSED here: nothing about the original connection's
                // retry may outlive the stream that carried it, and the carried delivery is the only
                // sender for this assignment from now on. (Closing admission cancels a pending retry
                // delay or permit wait; an already-admitted transport write keeps its own outcome,
                // exactly as every other drain's close does.)
                //
                // The heartbeat task state is RESTORED from the assignment so the orchestrator keeps
                // seeing this worker as busy on the same task: the reporter's own `finally` may have
                // cleared it already, and nothing else will set it again until the delivery completes.
                retained.Receipt.Retry?.CloseAdmission();
                _currentTaskId = retained.TaskId;
                _currentRole = retained.Role;

                // THE ONE CARRIED DELIVERY, started exactly once, by this CAS winner, and retained on
                // the assignment so every ownership transition joins it.
                retained.CarriedDelivery = DeliverCarriedAssignmentAsync(retained);

                teardownDrainFailure = null;
            }
            else if (streamLoss && retained is not null
                && (retained.State.IsCarried || retained.State.IsDelivered))
            {
                // ── STREAM LOSS WITH AN ALREADY-CARRIED (OR DELIVERED) ASSIGNMENT ────────────────────
                //
                // The assignment was carried by an EARLIER stream loss and its delivery task is
                // already running (or has finished): there is NOTHING to cancel or join here, and the
                // delivery is NEVER restarted. A DELIVERED assignment is finished by the EXIT RE-CHECK
                // in RunAsync, which runs AFTER this run's lexical stream disposal.
                //
                // The heartbeat task state is re-asserted for the CARRIED case: this worker is still
                // working on that task and the orchestrator must keep seeing it as busy. A delivered
                // assignment is already finished with its task and is deliberately NOT re-asserted.
                if (retained.State.IsCarried)
                {
                    _currentTaskId = retained.TaskId;
                    _currentRole = retained.Role;
                }

                teardownDrainFailure = null;
            }
            else
            {
                // ── TODAY'S TEARDOWN ────────────────────────────────────────────────────────────────
                //
                // Either this is not a stream loss at all (a handler failure, or anything after the
                // process token was cancelled), or the assignment already claimed its ordinary Ready
                // write (`ReadyStarted`, which is mutually exclusive with `Carried` by the CAS), or
                // nothing is retained. Stream shutdown must not leave a task running: Program disposes
                // the runner right after this returns, and a still-running turn holds the client
                // lifecycle lease, so the assignment is cancelled and drained as today, and the
                // heartbeat's task state is cleared as today.
                //
                // ORDERING PRECEDENCE. On the stream-loss path the connection was ALREADY retired at
                // the read-await site — the very first action there — so in this branch the connection
                // is retired BEFORE the drain rather than after it. Everything else in this teardown
                // keeps today's relative order: cancel, drain (execution, reporting, retransmission,
                // readiness write), clear the heartbeat state, then the idempotent Retire below.
                teardownDrainFailure = _activeAssignment is not null
                    ? await DrainRetainedForTeardownAsync()
                    : null;

                _currentTaskId = null;
                _currentRole = null;
            }

            // RETIRE ACCESS only AFTER the drain above. A report draining behind an EOF or a reader
            // failure with a LIVE token therefore still got its single Ready attempt; from here on,
            // any NEW operation on this connection fails disconnected instead of starting transport.
            // On the stream-loss path this is the idempotent second call — the early retire at the
            // read-await site already ran. The adoption record of THIS connection is dropped with it,
            // by reference identity, so a parked carried delivery can never be handed a connection
            // whose stream has just gone away.
            connection.Retire();
            ClearAdoption(connection);

            // ERROR PRECEDENCE. With a primary loop failure already propagating (a reader fault, a
            // cancelled read, a handler failure), that primary is what surfaces and the secondary
            // cancellation-cleanup failure is reported through guarded sanitized logging ONLY — a
            // logger failure can never replace the real failure. Without a primary, the deferred
            // failure propagates now, AFTER the clear, the retirement and BOTH joins above.
            PropagateOrReport(
                teardownDrainFailure, primaryFailure, TaskCancellationFailedMessage);
        }
    }

    /// <summary>The GUARDED diagnostic emitted once for the FIRST accepted receipt acknowledgement.</summary>
    private const string ReceiptConfirmedMessage = "Completion receipt confirmed by orchestrator for task";

    /// <summary>
    /// ONE pending response read, returned as a task so the loop can race it against the retained
    /// assignment's readiness fact. <c>null</c> means the stream ended (EOF).
    /// </summary>
    /// <remarks>
    /// The read itself is the SAME <see cref="IAsyncStreamReader{T}.MoveNext"/> call the previous
    /// <c>await foreach</c> made, and this method deliberately does not inspect
    /// <see cref="IAsyncStreamReader{T}.Current"/>: the loop reads it only after this task
    /// completed, so a message can never be observed half-installed.
    /// </remarks>
    private static async Task<OrchestratorMessage?> ReadNextMessageAsync(
        IAsyncStreamReader<OrchestratorMessage> reader, CancellationToken ct)
    {
        if (!await reader.MoveNext(ct))
            return null;

        return reader.Current;
    }

    /// <summary>
    /// The readiness wait for the CURRENTLY retained assignment: the assignment's ONE armed
    /// observation of its ordinary-Ready readiness predicate — eligibility, plus (in the gated shape)
    /// the mapped/armed Complete whose original local write terminated and a matching receipt
    /// confirmation. Nothing retained means a never-completing task, and a SETTLED assignment also
    /// yields a never-completing await — so the loop neither polls, re-arms, nor accumulates waiters,
    /// and a started readiness write is never raced here (the ownership transitions join it instead).
    /// </summary>
    private Task OrdinaryReadinessWait() =>
        _activeAssignment is { } assignment
            ? assignment.OrdinaryReady.Arm()
            : NeverCompletingTask;

    /// <summary>
    /// A task that never completes, used as the idle arm of the loop's read/readiness race so no
    /// polling or timer is ever required.
    /// </summary>
    private static readonly Task NeverCompletingTask = new TaskCompletionSource().Task;

    /// <summary>
    /// THE ONE PLACE THE GATED ORDINARY READINESS IS DECIDED: <c>true</c> only when the ACCEPTED
    /// registration behind <paramref name="connection"/> carried BOTH negotiated facts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ACK answer alone is the previous tracking-only behavior — the receipt is still recorded, and
    /// the ordinary Ready is still ungated. The readiness requirement alone, and neither fact at all,
    /// are the fully legacy behavior. Only BOTH together gate the ordinary Ready.
    /// </para>
    /// <para>
    /// It reads the two IMMUTABLE per-connection facts directly and derives nothing: no version, no
    /// capability and no model is consulted, and no third fact is inferred from these two. Because
    /// both are the assignment's OWN connection's facts, a successor registration can never gate — or
    /// ungate — this assignment's readiness.
    /// </para>
    /// </remarks>
    /// <param name="connection">The connection the assignment arrived on.</param>
    /// <returns><c>true</c> when the ordinary Ready must wait for a confirmed receipt.</returns>
    private static bool OrdinaryReadyGateEnabled(WorkerConnection connection) =>
        connection.CompletionReceiptAckEnabled && connection.CompletionReadyRequired;

    /// <summary>
    /// SETTLES the retained assignment's ordinary readiness EXACTLY ONCE — when its predicate holds —
    /// and STARTS the single readiness write from it — WITHOUT awaiting that write here, so the
    /// settlement can never hold a caller (the response loop, or an ownership transition) off. Returns
    /// the ONE retained write for the caller to join, or <c>null</c> when the predicate does not hold
    /// or the shared claim was already taken.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The settlement is the slot's OWN transition, so it can never be lost or duplicated by a
    /// concurrent participant: the FIRST settler takes the assignment's SHARED single-flight Ready
    /// claim and retains the one write, and every later settler just observes what is already
    /// retained. The ACTUAL write goes through the existing <see cref="SendWorkerReady"/> on the
    /// assignment's ORIGINAL connection object and its ORIGINAL stream token, so it can never be
    /// retargeted at a successor assignment or connection. The write is started ONCE: a failed (or
    /// cancelled) write consumes the shared claim and is never retried.
    /// </para>
    /// <para>
    /// THE SAME GATE FOR EVERY CALLER. This is the single settlement entry point, so the response loop
    /// and all three ownership-transition drains decide the ordinary Ready from IDENTICAL facts. In the
    /// negotiated mode an unconfirmed receipt is simply a predicate that does not hold, so the ordinary
    /// Ready is withheld — including across an EOF — while nothing here waits for an acknowledgement.
    /// </para>
    /// <para>
    /// ERROR PRECEDENCE. A failed ordinary Ready write is NONFATAL, exactly as the previous
    /// ordinary-Ready attempt's treatment was: it is reported in sanitized, guarded form and never
    /// propagates, so it can never replace a reader/handler primary or a deferred
    /// cancellation-callback failure.
    /// </para>
    /// </remarks>
    /// <param name="ordinaryReady">The retained assignment's ordinary-readiness slot.</param>
    private Task? SettleOrdinaryReady(OrdinaryReadySlot ordinaryReady) =>
        ordinaryReady.Settle(StartOrdinaryReadyWrite);

    /// <summary>
    /// STARTS the single ordinary readiness write for one assignment: one <c>WorkerReady</c> on the
    /// assignment's ORIGINAL connection object and its ORIGINAL stream token.
    /// </summary>
    /// <remarks>
    /// The ORIGINAL write task is returned, not a continuation of it, so the slot retains the very
    /// task every ownership transition joins and the write is never abandoned. Nothing awaits it
    /// here: its failure is NONFATAL, exactly as the previous ordinary-Ready attempt's treatment
    /// was — an ownership transition joins it and reports any fault through the EXISTING sanitized
    /// drain diagnostics, so it can never replace a reader/handler primary or a deferred
    /// cancellation-callback failure.
    /// </remarks>
    private Task StartOrdinaryReadyWrite(WorkerConnection connection, CancellationToken streamToken) =>
        SendWorkerReady(connection, streamToken);

    /// <summary>
    /// THE COMPLETION-RECEIPT ACK CASE. It records — idempotently, on the assignment the loop still
    /// retains — that the orchestrator acknowledged durably retaining that task's completion
    /// evidence, and it notifies the retained assignment's ordinary-readiness observation so that a
    /// Ready which was waiting for exactly this fact can proceed. It does nothing else whatsoever.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ACCEPTANCE IS NARROW. The acknowledgement is applied ONLY when this loop's connection
    /// negotiated the feature, the retained assignment belongs to THIS ORIGINAL connection, that
    /// assignment was ARMED by its single Complete attempt, and BOTH identities match EXACTLY and
    /// ORDINALLY: the wire <c>TaskId</c> against the retained assignment's task ID and the wire
    /// <c>WorkerId</c> against this connection's assigned identity. Nothing is parsed, trimmed,
    /// normalized or case-folded, and an acknowledgement is NEVER inferred from a completed write,
    /// a <c>Ready</c>, or the arrival of a successor assignment.
    /// </para>
    /// <para>
    /// THE NOTIFICATION IS PURE AND ONLY IN THE GATED SHAPE. It re-evaluates the assignment's own
    /// readiness predicate for whoever is parked on it — an acknowledgement that arrived while the
    /// Complete write was still pending simply parks the reader until that write's termination is
    /// also observed, and one that arrived first wakes a reader that already has every other fact. It
    /// never awaits, never joins the write, never takes the Ready claim and never writes: the actual
    /// Ready is still produced by the ONE settlement. In the ACK-only shape the slot's predicate does
    /// not include the receipt, so this is genuinely a no-op and tracking is unchanged.
    /// </para>
    /// <para>
    /// EVERYTHING ELSE IS UNTOUCHED. It never clears the retained result or the ownership slot,
    /// never cancels or joins execution, reporting or the retransmission task, never resets the
    /// runner and never sends a Ready. THIS HANDLER ITSELF WRITES NOTHING: the only completion
    /// re-send anywhere is the assignment's own retransmission task, which this handler merely STOPS
    /// from admitting further attempts. In particular an acknowledgement that arrives while the
    /// actual Complete write is STILL PENDING is simply latched: the write keeps its own send permit,
    /// its own outcome and its own owner. Receipt confirmation and local write success stay SEPARATE
    /// facts, so an early ACK can never convert a later failed or cancelled write into a success.
    /// </para>
    /// <para>
    /// IT ALSO CLOSES THE GATED ASSIGNMENT'S RETRY ADMISSION, as part of the SAME atomic acceptance —
    /// and nothing more. A pending retransmission delay or permit wait is cancelled, so no FURTHER
    /// attempt is admitted; the retry task is NOT awaited (the drains join it) and an
    /// already-admitted transport write is NOT cancelled, because that write is awaited with the
    /// ORIGINAL stream token. This is the ACK-side half of the arbitration: a confirmation that wins
    /// performs no further retransmission, while one that loses to an attempt already admitted leaves
    /// that attempt to finish.
    /// </para>
    /// <para>
    /// A duplicate, a wrong task, a wrong worker, an unarmed or already-cleared assignment, a
    /// disabled connection and a delivery that belongs to a previous connection are all no-ops:
    /// none of them may confirm some OTHER assignment. Because the wire acknowledgement carries no
    /// generation nonce, this is deliberately NOT a claim of assignment-generation safety when a
    /// task ID and worker ID are deliberately reused — identity matching is all the protocol
    /// affords.
    /// </para>
    /// </remarks>
    /// <param name="connection">The ORIGINAL connection this loop was started for.</param>
    /// <param name="ack">The acknowledgement exactly as received.</param>
    private void HandleCompletionReceiptAck(WorkerConnection connection, CompletionReceiptAck ack)
    {
        // NEGOTIATION FIRST: a connection that was not answered "enabled" expects none of these.
        if (!connection.CompletionReceiptAckEnabled)
            return;

        // The retained assignment is the only place a receipt can land. A cleared slot (a delayed
        // acknowledgement after the existing ownership clear) is ignored — this slice makes no
        // cross-stream or post-replacement retention promise.
        if (_activeAssignment is not { } assignment)
            return;

        // ORDINAL-EXACT identities, used verbatim. No parsing, no trimming, no normalization.
        if (!string.Equals(assignment.TaskId, ack.TaskId, StringComparison.Ordinal)
            || !string.Equals(connection.AssignedId, ack.WorkerId, StringComparison.Ordinal))
        {
            return;
        }

        // ARMED + SAME ORIGINAL CONNECTION, decided by the tracker's single atomic transition. It
        // returns true for the FIRST accepted receipt only, so duplicates change nothing.
        //
        // IN THE GATED SHAPE THE SAME ACCEPTANCE ALSO CLOSES FUTURE RETRY ADMISSION, atomically under
        // the retransmitter's own lock — the transition and the close are one step, so the pending
        // retry can never slip an attempt in between. This awaits nothing, joins nothing and cancels
        // no admitted transport write; only a pending delay or permit wait is stopped. A wrong task,
        // a wrong worker, a duplicate acknowledgement and a delivery on another connection all return
        // false here and leave the receipt — and admission — untouched.
        var receipt = assignment.Receipt;
        var confirmed = receipt.Retry is { } retry
            ? retry.TryConfirm(connection)
            : receipt.TryConfirm(connection);
        if (!confirmed)
            return;

        // WAKE — the ONLY other thing this case does, and only in the GATED shape. In that shape the
        // ordinary readiness was withheld pending exactly this fact, so the parked readiness
        // observation is re-evaluated and completes now that the predicate holds. This is pure
        // notification: it does NOT await, does NOT join (or resend, or complete) the Complete write,
        // does NOT consume the Ready claim, and touches neither the retained result nor the ownership
        // slot. In the UNGATED shape there is nothing to wake, so the receipt stays tracking-only.
        assignment.OrdinaryReady.NoteReceiptConfirmed();

        // ONE GUARDED DIAGNOSTIC, for the first accepted receipt only. It states RETENTION of the
        // completion evidence and nothing more — not processing, not phase advancement, not that
        // the local transport write succeeded — and it carries no completion payload, no
        // provisioned value and no exception text.
        try
        {
            _log.Info($"{ReceiptConfirmedMessage} {assignment.TaskId}");
        }
        catch
        {
            // A diagnostic must never affect the loop's outcome.
        }
    }

    /// <summary>
    /// Waits for EVERY owned task of an assignment — its EXECUTION, its connection-bound REPORTING
    /// and (in the gated shape) its RETRANSMISSION — to finish, optionally cancelling the assignment
    /// first, then disposes its <see cref="CancellationTokenSource"/>. Never throws for cancellation:
    /// the whole point is to reach a quiescent state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ALL JOINS ALWAYS HAPPEN, in that order, WITHOUT a caller token — a caller token can never
    /// make a join vacuous, and no task is ever abandoned. An EXECUTION fault cannot skip the
    /// REPORTING join (it is captured, exactly as before) and neither can a throwing
    /// cancellation callback: that failure is CAPTURED and returned to the caller, to be re-raised
    /// <em>after</em> every join and the disposal below, rather than skipping them.
    /// </para>
    /// <para>
    /// RETRY ADMISSION IS CLOSED AND THE PENDING WAIT CANCELLED FIRST, before any join, and
    /// independently of any receipt fact: a retained retransmission parks in a fixed delay or in the
    /// send gate's permit queue, and both are stopped by that call, so the retry join terminates
    /// promptly even when reporting never published a payload and even when NO acknowledgement ever
    /// arrives — an ACK is never a prerequisite for drain completion. The retry-lifetime source is
    /// released only after its task has been joined.
    /// </para>
    /// <para>
    /// Ordinary cancellation tolerance and the sanitized treatment of task faults are unchanged for
    /// every task: an <see cref="OperationCanceledException"/> is expected, and any other fault is
    /// reported in sanitized form (guarded, so a diagnostic can never skip the remaining join or
    /// the disposal) rather than propagating into the message loop or teardown path. A producer
    /// exception the reporting task merely OBSERVED is not re-raised there, so it is reported here
    /// exactly once — from the execution join — and never twice.
    /// </para>
    /// </remarks>
    /// <param name="assignment">The assignment to drain.</param>
    /// <param name="cancelFirst">
    /// <c>true</c> to request cancellation before awaiting (cancel handling and stream teardown);
    /// <c>false</c> to simply await an assignment that is already finishing.
    /// </param>
    /// <returns>
    /// The deferred cancellation-cleanup failure to propagate after cleanup, or <c>null</c> when
    /// none arose (success, no cancellation requested, or an already-disposed source).
    /// </returns>
    private async Task<Exception?> DrainAssignmentAsync(ActiveAssignment assignment, bool cancelFirst)
    {
        // CLOSE RETRANSMISSION ADMISSION AND CANCEL ITS PENDING WAIT FIRST — before the assignment's
        // own cancellation is even requested and therefore before any join, on EVERY drain (matching
        // cancel, replacement, EOF, reader fault, run cancellation) and unconditionally: a non-gated
        // assignment simply has no retry, and a drain that arrives while reporting has not yet
        // published its payload closes admission just the same. This is what makes the retry join
        // below terminate: the retry task parks in a five-second delay or in the send gate's permit
        // queue, and the retry-lifetime cancellation stops BOTH. It is deliberately INDEPENDENT of any
        // receipt fact — an acknowledgement is NEVER a prerequisite for drain completion. The call is
        // synchronous, awaits nothing, is fault-contained (a throwing cancellation callback is
        // swallowed there rather than allowed to skip a join) and touches no transport: an
        // already-admitted write is awaited with the ORIGINAL stream token, so this can never cancel
        // it.
        assignment.Receipt.Retry?.CloseAdmission();

        // CAPTURE NEXT: a callback failure must not bypass either join or the disposal below.
        var deferredCancellationFailure = cancelFirst
            ? await CaptureCancellationFailureAsync(assignment.Cts)
            : null;

        // The EXECUTION first, then the REPORTING that awaits it — each captured, so neither a
        // fault nor a guarded diagnostic can skip what follows. Joining the reporting FIRST is
        // load-bearing: its own termination is what PUBLISHES the ordinary readiness eligibility, so
        // by the time this join returns that fact is final (either published, or never to be).
        ReportIfPresent(
            await CaptureJoinFailureAsync(assignment.Execution), DrainObservedFaultMessage);
        ReportIfPresent(
            await CaptureJoinFailureAsync(assignment.Reporting), DrainObservedFaultMessage);

        // THEN THE RETRANSMISSION TASK — the third owned task in the gated shape. Its admission was
        // already closed and its pending wait already cancelled above, so this join reaches
        // termination without waiting for any interval and without waiting for an acknowledgement. It
        // is joined BEFORE the readiness settlement below, so a retry can never be writing while the
        // single Ready is started and can never outlive the ownership clear that follows this drain.
        if (assignment.Retry is { } retryTask)
        {
            ReportIfPresent(
                await CaptureJoinFailureAsync(retryTask), DrainObservedFaultMessage);
        }

        // THEN THE CARRIED DELIVERY — the FOURTH owned task, present only for an assignment that a
        // stream loss carried. It OBSERVES the reporting task joined above and waits on the ADOPTED
        // publication with the assignment token, which the cancellation above has already ended, so
        // this join terminates without a delivery and without waiting for any connection. It is
        // joined BEFORE the readiness settlement below so no carried write can be in flight while the
        // ordinary settlement runs, and before the CTS disposal so it never observes a disposed
        // source. A non-carried assignment has no such task, so this is a no-op for it.
        if (assignment.CarriedDelivery is { } carriedDelivery)
        {
            ReportIfPresent(
                await CaptureJoinFailureAsync(carriedDelivery), DrainObservedFaultMessage);
        }

        // THEN SETTLE AND JOIN THE ORDINARY READINESS WRITE — the third owned task an assignment can
        // now have outstanding. Settlement is EXACTLY ONCE and shared with the response loop, and it
        // consults the SAME gate: a transition that sees the report terminate before the loop could
        // therefore still start the single Ready (it is never lost), while a loop that already started
        // it leaves the shared claim consumed (so the cancel fallback below can never duplicate it). A
        // predicate that does not hold — in the negotiated mode an unconfirmed receipt — settles
        // nothing and WAITS for nothing: no drain ever blocks on an acknowledgement.
        var readinessWrite = SettleOrdinaryReady(assignment.OrdinaryReady);
        if (readinessWrite is not null)
        {
            ReportIfPresent(
                await CaptureJoinFailureAsync(readinessWrite), DrainObservedFaultMessage);
        }

        // THE RETRY LIFETIME SOURCE IS RELEASED ONLY NOW — after its task joined. A source that a
        // still-running retry could observe is therefore never disposed underneath it.
        assignment.Receipt.Retry?.Dispose();

        // Disposal is attempted AFTER every join and runs even when a deferred cancellation failure
        // is waiting to propagate — the deferred failure surfaces only once resources are released.
        assignment.Cts.Dispose();

        return deferredCancellationFailure;
    }

    /// <summary>The sanitized report message for a fault observed while draining an assignment.</summary>
    private const string DrainObservedFaultMessage = "Task drain observed a fault";

    /// <summary>The sanitized report message for a failed CARRIED completion delivery attempt.</summary>
    private const string CarriedDeliveryFailedMessage = "Carried completion delivery failed";

    /// <summary>The sanitized report message for a failed CARRIED Ready write.</summary>
    private const string CarriedReadyFailedMessage = "Carried readiness write failed";

    /// <summary>The GUARDED diagnostic emitted once when a carried completion is delivered.</summary>
    private const string CarriedDeliveredMessage = "Carried completion delivered for task";

    /// <summary>
    /// Publishes ONE informational diagnostic under the SAME guard the sanitized failure reporters
    /// use: a diagnostic must never affect the outcome of the work it describes. It carries no
    /// completion payload, no provisioned value and no exception text.
    /// </summary>
    /// <param name="message">The static, secret-free message.</param>
    private void TryLogInfo(string message)
    {
        try
        {
            _log.Info(message);
        }
        catch
        {
            // A diagnostic must never affect the delivery's outcome.
        }
    }

    /// <summary>
    /// DRAINS any RETAINED assignment — cancelling it first — including the CARRIED delivery
    /// continuation it owns. Used by the process's final cleanup and by every path that abandons a
    /// carried assignment (a refused adoption, a rejected registration, explicit cancellation).
    /// </summary>
    /// <remarks>
    /// It is the public-ish counterpart of the loop's teardown drain and adds NO new ownership rule:
    /// it takes the retained assignment, cancel-drains it through the EXISTING
    /// <see cref="DrainAssignmentAsync"/> (which now also joins the carried delivery), clears the
    /// heartbeat's task state and the ownership slot, then re-raises any deferred cancellation-cleanup
    /// failure with the EXISTING evidence-preserving rule. It is a no-op when nothing is retained, so a
    /// caller may invoke it unconditionally.
    /// </remarks>
    /// <exception cref="Exception">
    /// The deferred cancellation-cleanup failure, re-raised with its ORIGINAL evidence, exactly as
    /// the existing drains do.
    /// </exception>
    internal async Task DrainCarriedAssignmentAsync()
    {
        if (_activeAssignment is null)
            return;

        Exception? deferredCancellationFailure = null;
        try
        {
            (_, deferredCancellationFailure) = await DrainRetainedForMatchingCancelAsync();
        }
        finally
        {
            // The heartbeat state is cleared on EVERY path — after a deferred cancellation failure
            // too — because the assignment is no longer retained either way.
            _currentTaskId = null;
            _currentRole = null;
        }

        RethrowDeferred(deferredCancellationFailure);
    }

    /// <summary>
    /// THE CARRIED DELIVERY — ONE owned continuation per carried assignment. It observes the
    /// assignment's ORIGINAL reporting task to termination, then delivers the retained completion and
    /// the assignment's single Ready on an ADOPTED connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// STEP 0 — OBSERVE, NEVER JOIN. The reporting task is awaited directly and every outcome is
    /// observed; it is deliberately NOT joined by the carry teardown (nor by this method's callers),
    /// because a write park on the OLD connection is released by that run's transport disposal at the
    /// end of the run — long after the teardown has returned. Awaiting the reporting HERE is safe:
    /// this continuation is owned and joined by every ownership transition, and a pending
    /// old-connection write is released by that disposal. The retained terminal result is then read;
    /// it may legitimately be absent (a provisioning failure, a cancellation or a mapping failure
    /// produced none).
    /// </para>
    /// <para>
    /// STEPS 0-3 USE THE ASSIGNMENT TOKEN (including the send-gate wait), so a cancellation before
    /// the Complete write is initiated ends this task with NO Complete and WITHOUT consuming the
    /// Ready claim. An already-initiated write cannot be retracted — that is inherent, exactly as it
    /// is for an ordinary Complete. STEP 4 (the Ready) instead uses the ADOPTED RUN'S STREAM TOKEN,
    /// so assignment cancellation can never suppress a Ready that has already been claimed.
    /// </para>
    /// <para>
    /// STEP 1 — ONE ADOPTED TARGET, NO SPIN. A delivery attempt that failed on a connection returns
    /// to the wait, and the wait only returns a connection OTHER than the one last tried, so a
    /// retry can never busy-loop against the same broken stream. A failure that is a cancellation
    /// ends the task instead.
    /// </para>
    /// <para>
    /// STEP 3 — DELIVERED. After the successful Complete (or when there was no result at all) the
    /// assignment's state moves <c>Carried → Delivered</c> and the heartbeat's task state is cleared.
    /// From that point the assignment token is not observed again: what remains is the Ready
    /// attempt, which belongs to the adopting run.
    /// </para>
    /// <para>
    /// STEP 4 — THE ONE READY, THROUGH THE EXISTING CLAIM. The same single-flight
    /// <see cref="ReadyClaim"/> the explicit-cancel fallback uses decides whether a Ready is sent at
    /// all, so exactly ONE Ready is ever attempted per assignment. The claim is followed immediately
    /// by <see cref="ActiveAssignment.MarkCarriedReadyStarted"/> and then by the write, with NO
    /// cancellation check in between: a successor assignment is authorized by that fact, so it must
    /// not be published without the write actually being started.
    /// </para>
    /// <para>
    /// IT NEVER RETHROWS. Every outcome of the carried Ready — success, failure and cancellation
    /// alike — is observed and, for a failure, reported through ONE guarded sanitized diagnostic.
    /// That matches a failed ordinary Ready exactly: one attempt, no retry, and no run-ending
    /// mechanism of its own. A failed carried Ready leaves this assignment retained as Delivered with
    /// <see cref="ActiveAssignment.CarriedReadyStarted"/> set, and one of the EXISTING transitions
    /// finishes it: the stream ends (the Delivered teardown plus the exit re-check clear it), a
    /// successor assignment arrives (authorized by that flag, taking the existing replacement drain),
    /// or the process shuts down (<see cref="DrainCarriedAssignmentAsync"/>).
    /// </para>
    /// </remarks>
    /// <param name="assignment">The carried assignment this continuation belongs to.</param>
    private async Task DeliverCarriedAssignmentAsync(ActiveAssignment assignment)
    {
        var assignmentToken = assignment.Cts.Token;

        // The adopted connection and stream token the Ready attempt will use. STEP 3 always leaves
        // one: the connection whose Complete write succeeded, or the one that had no result to send.
        AdoptedConnection? deliveredOn = null;

        try
        {
            // STEP 0 — OBSERVE the original reporting to termination (any outcome), then read the
            // retained terminal result. It may be absent.
            try
            {
                await assignment.Reporting;
            }
            catch
            {
                // Observed only: the reporting task keeps its own evidence for whoever joins it.
            }

            // STEP 1 — the FIRST adopted connection (nothing has been tried yet).
            WorkerConnection? lastTried = null;
            while (true)
            {
                var adopted = await AwaitAdoptedConnectionAsync(lastTried, assignmentToken);

                // STEPS 2-3 — deliver the retained Complete ONCE on this connection.
                if (assignment.TerminalResult.Result is { } result)
                {
                    try
                    {
                        // The TEST SEAM's instant: immediately BEFORE the Complete write, with the
                        // ASSIGNMENT token the send below will use.
                        if (CarriedBeforeCompleteSendHook is { } beforeSend)
                            await beforeSend(assignmentToken);

                        await SendAsync(
                            adopted.Connection,
                            new WorkerMessage
                            {
                                WorkerId = adopted.Connection.AssignedId,
                                Complete = GrpcMapper.ToGrpc(result),
                            },
                            assignmentToken);

                        TryLogInfo($"{CarriedDeliveredMessage} {assignment.TaskId}");
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancellation ends this task: a cancelled assignment has no delivery to
                        // make, and it must not be retried on another connection. The Ready claim is
                        // left UNCONSUMED.
                        return;
                    }
                    catch (Exception ex)
                    {
                        // A NON-cancellation failure is reported sanitized and the wait resumes for a
                        // DIFFERENT adopted connection, so there is no spin against the same stream.
                        TryLogSanitized(CarriedDeliveryFailedMessage, ex);
                        lastTried = adopted.Connection;
                        continue;
                    }
                }

                deliveredOn = adopted;
                break;
            }
        }
        catch (OperationCanceledException)
        {
            // The assignment was cancelled while waiting for an adopted connection: this task ends
            // with NO Complete and WITHOUT consuming the Ready claim.
            return;
        }

        // STEP 3 — CARRIED → DELIVERED, then the heartbeat state is cleared. The transition happens
        // here, BEFORE the Ready, because the delivery of the result is what makes this assignment
        // delivered: the Ready is the adopted run's last act and must stay possible even if the
        // process cancels the assignment right now. After this point the assignment token is NOT
        // observed again.
        assignment.State.TryDeliver();
        _currentTaskId = null;
        _currentRole = null;

        // THE TEST SEAM's instant: after the Complete write succeeded and BEFORE the Ready claim.
        if (CarriedBeforeReadyClaimHook is { } beforeClaim)
            await beforeClaim();

        // STEP 4 — THE ONE READY, THROUGH THE EXISTING SINGLE-FLIGHT CLAIM. A LOST claim sends no
        // Ready at all (exactly one Ready is ever attempted per assignment). There is deliberately NO
        // cancellation check between the winning claim and the write: the started-write fact below
        // authorizes a successor, so it must never be published for a write that was not started.
        if (!assignment.Ready.TryClaim())
            return;

        var adoptedConnection = deliveredOn!.Connection;
        var adoptedStreamToken = deliveredOn.StreamToken;
        assignment.MarkCarriedReadyStarted();

        try
        {
            // The ADOPTED RUN'S STREAM TOKEN — never the assignment token — so cancelling the
            // assignment can never suppress a Ready that has already been claimed.
            await SendWorkerReady(adoptedConnection, adoptedStreamToken);
        }
        catch (Exception ex)
        {
            // A failed carried Ready has EXACTLY the semantics of a failed ordinary Ready today: ONE
            // guarded sanitized diagnostic, no retry, and it does NOT end the run. The assignment
            // stays retained as Delivered with the started-write fact set.
            TryLogSanitized(CarriedReadyFailedMessage, ex);
        }
    }

    #region Assignment execution and config-repo preparation

    /// <summary>
    /// EXECUTION ONLY — runs one assignment through an executor and RETAINS its terminal result
    /// under the assignment owner. Shared by BOTH dispatch forms (the legacy, seam-free executor
    /// and the seam-carrying one) so the two can never drift apart in what they retain.
    /// </summary>
    /// <remarks>
    /// NOTHING HERE TOUCHES THE CONNECTION. The EXACT complete domain <c>TaskResult</c> the
    /// executor returned is published into the assignment-local holder ONCE, before any payload
    /// mapping and before any transport await; the connection-bound reporting
    /// (<see cref="ReportAssignmentAsync"/>) then merely consumes what is already retained. That
    /// separation is what keeps a blocked, failed or cancelled completion write from holding — or
    /// changing the outcome of — the execution itself: the result is neither truncated nor replaced
    /// by a synthesized transport-failure result, and execution is never retried. Completed, Failed
    /// and Cancelled results are retained alike. If setup or execution throws before a result
    /// exists, the holder stays EMPTY rather than carrying a fabricated completion, and the
    /// exception propagates to the execution task's existing handlers unchanged.
    /// </remarks>
    /// <param name="executor">The executor to run — already fully constructed.</param>
    /// <param name="task">The domain task.</param>
    /// <param name="terminalResult">
    /// The assignment-local holder this execution publishes its terminal result into. Passed in by
    /// the execution task's closure — never discovered through the ownership slot, which may not
    /// yet hold this assignment when the execution starts.
    /// </param>
    /// <param name="bodyToken">The ASSIGNMENT's token, which cancels the execution itself.</param>
    private static async Task ExecuteAssignmentAsync(
        TaskExecutor executor,
        WorkTask task,
        TerminalResultHolder terminalResult,
        CancellationToken bodyToken)
    {
        var result = await executor.ExecuteAsync(task, bodyToken);

        // RETAIN — the exact, complete result, before any mapping and before any transport exists.
        terminalResult.Publish(result);
    }

    /// <summary>
    /// CONNECTION-BOUND REPORTING for ONE assignment: it awaits the ORIGINAL execution task,
    /// consumes the already-retained terminal result for the Complete mapping, write and completion
    /// diagnostic, publishes the termination of that write, clears the heartbeat's task state, and
    /// PUBLISHES the ordinary-Ready eligibility for its owner to settle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ASSIGNMENT-LOCAL INPUTS ONLY. Every value it needs — the ORIGINAL execution task, the domain
    /// task, the EXPECTED connection, the holder, the receipt tracker and the readiness slot — is
    /// passed in by the assignment handler that created them. Nothing is discovered through the
    /// ownership slot and nothing is re-read from the published connection, so a report can never be
    /// retargeted to a later registration.
    /// </para>
    /// <para>
    /// THE PRODUCER JOIN IS UNCONDITIONAL. The execution task is awaited directly (never through a
    /// cancellation-skippable continuation), so a producer that was CANCELLED BEFORE ITS BODY EVER
    /// STARTED is still observed here.
    /// </para>
    /// <para>
    /// READINESS IS PUBLISHED, NOT SENT. At EXACTLY the point where the old body decided whether to
    /// make its ordinary Ready attempt — the condition that gates it, i.e. the execution terminated
    /// NORMALLY — this method publishes the eligibility instead of writing. It is NOT inferred from
    /// "a result exists": a handled provisioning failure produces NO result, no Complete, the
    /// existing exception handling (OCE-tolerant/sanitized) and yet still publishes the eligibility.
    /// It is NOT inferred from local send success either: a failed or cancelled Complete leaves the
    /// eligibility published (and, separately, its own terminated-write fact published) exactly as the
    /// pre-split body would still have attempted Ready. A pre-start execution cancellation or an
    /// escaping diagnostic leaves the eligibility UNPUBLISHED, so the shared Ready claim stays
    /// UNCONSUMED and a matching cancel can still emit its fallback single Ready. Reporting never
    /// awaits an acknowledgement, readiness, or the readiness WRITE: it terminates independently of
    /// it, and it makes its existing ONE Complete attempt in every mode.
    /// </para>
    /// </remarks>
    /// <param name="execution">The ORIGINAL execution task this report belongs to.</param>
    /// <param name="task">The domain task (its ID is used for the completion diagnostic).</param>
    /// <param name="connection">
    /// The EXPECTED connection this assignment belongs to. The Complete write consumes THIS object's
    /// stream and identity, so a report can never be written on a different registration than the
    /// one the assignment arrived on.
    /// </param>
    /// <param name="terminalResult">The assignment-local holder carrying the retained result.</param>
    /// <param name="receipt">
    /// The assignment-local receipt tracker. It is ARMED only here, only on an ENABLED connection,
    /// only once an exact produced result has been mapped, and only immediately before the single
    /// existing Complete send — never for an absent result, a failed mapping, or a merely installed
    /// or running assignment. Reporting NEVER awaits an acknowledgement: arming is a synchronous
    /// publication and the send below is unchanged.
    /// </param>
    /// <param name="ordinaryReady">
    /// The assignment-local ordinary-readiness slot this report PUBLISHES into — the eligibility at
    /// the old ordinary-Ready point, and the termination of the single Complete write. The owner (the
    /// response loop, or an ownership transition) is the only observer, and it starts the single
    /// readiness write once the slot's whole predicate holds.
    /// </param>
    /// <remarks>
    /// FREEZE ONCE, SEND CLONES. On the successful-mapping path only, the mapped
    /// <see cref="TaskComplete"/> is handed to the assignment's retransmitter ONCE (a non-gated
    /// assignment's tracker carries none, so nothing is frozen there) and the receipt is armed
    /// AFTERWARDS. Every send built here and by that retransmitter is a FRESH deep clone of the
    /// private envelope, so no writer is ever handed the snapshot itself. The retained
    /// <see cref="TaskResult"/> keeps its exact identity: it is never cloned, re-mapped or replaced.
    /// </remarks>
    private async Task ReportAssignmentAsync(
        Task execution,
        WorkTask task,
        WorkerConnection connection,
        TerminalResultHolder terminalResult,
        CompletionReceiptTracker receipt,
        OrdinaryReadySlot ordinaryReady)
    {
        var executionTerminatedNormally = false;
        try
        {
            executionTerminatedNormally = await ObserveExecutionAsync(execution);

            // CONSUME what is already retained. An empty holder (a producer that failed before a
            // result existed) reports nothing at all rather than fabricating a completion.
            if (executionTerminatedNormally && terminalResult.Result is { } result)
            {
                // THE MAPPING FIRST: a mapping that throws leaves the assignment UNARMED, exactly
                // like an absent result does.
                var completion = GrpcMapper.ToGrpc(result);

                // FREEZE ONCE, BEFORE ARMING. The captured assigned worker identity and the SINGLE
                // mapped payload become a privately owned envelope that no writer ever receives: the
                // ORIGINAL Complete below and every later retransmission each send a FRESH deep clone
                // of it, so nothing a writer does can alter what the next attempt re-sends — and the
                // retained TaskResult is never re-mapped, cloned or replaced. A retransmitter exists
                // only in the gated shape, so freezing is a no-op for every other connection.
                var retry = receipt.Retry;
                retry?.Freeze(connection.AssignedId, completion);

                // ARM — on the ENABLED connection only, with the exact mapped result in hand and
                // the single Complete attempt about to invoke the EXISTING send. Nothing waits on
                // it: an acknowledgement that arrives while the write below is still pending is
                // latched by the reader without touching this task, the send permit or the owner.
                if (connection.CompletionReceiptAckEnabled)
                    receipt.Arm();

                // THE ORIGINAL LOCAL WRITE, OBSERVED TO TERMINATION. This is the single Complete
                // attempt and NOTHING here changes its outcome: success, failure and cancellation
                // each keep propagating exactly as before, the send permit is still released by the
                // send itself, the error is never replaced and the retained result is never altered.
                // The `finally` adds only the FACT that the attempt has terminated — which the GATED
                // ordinary readiness needs before it can authorize a Ready, because an
                // acknowledgement means the evidence was durably retained, not that this local write
                // succeeded. Publishing that fact consumes no Ready claim, writes nothing and waits
                // for nothing.
                //
                // THE SENT MESSAGE IS A FRESH CLONE of the frozen envelope (or the just-mapped value
                // on a non-gated connection, which has no retransmitter at all): the private snapshot
                // is never handed to the writer.
                try
                {
                    await SendAsync(
                        connection,
                        retry is null
                            ? new WorkerMessage
                            {
                                WorkerId = connection.AssignedId,
                                Complete = completion,
                            }
                            : retry.NextMessage(),
                        ordinaryReady.StreamToken);

                    _log.Info($"Task {task.TaskId} completed ({result.Status})");
                }
                finally
                {
                    ordinaryReady.PublishCompleteWriteTerminated();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Sanitized: the completion write crosses the gRPC boundary, whose status details can
            // echo provisioned configuration back to the worker.
            _log.Error($"Task execution failed [{SafeExceptionLog.Describe(ex)}]");
        }
        finally
        {
            // THE EXISTING LOGICAL POINT: after completion reporting. The heartbeat state is cleared
            // here FIRST and the eligibility is published AFTERWARDS, so the heartbeat state is
            // always already cleared by the time the write the eligibility leads to can be observed.
            //
            // CLEAR-THEN-RECHECK. A carried assignment must keep being reported as busy even when its
            // reporter reaches this point — the reporter may well finish BEFORE the stream loss that
            // carries the assignment, and its own cleanup would otherwise silently blank a heartbeat
            // state that the carried assignment still needs. So: clear first (exactly as today), then
            // re-read the retained assignment's state and, only for a CARRIED one, set the task state
            // again from the assignment. Nothing else is resurrected: a Delivered assignment has
            // finished with its task and stays cleared, and a non-carried assignment keeps today's
            // cleared state.
            //
            // The retained assignment is only consulted for a CARRIED one whose task ID is THIS
            // report's own task, so a report can never resurrect a DIFFERENT assignment's task state
            // (which is reachable only in a fixture that drives this method with no installed owner).
            _currentTaskId = null;
            _currentRole = null;
            if (_activeAssignment is { State.IsCarried: true } retained
                && string.Equals(retained.TaskId, task.TaskId, StringComparison.Ordinal))
            {
                _currentTaskId = retained.TaskId;
                _currentRole = retained.Role;
            }

            if (executionTerminatedNormally)
                ordinaryReady.PublishEligibility();
        }
    }

    /// <summary>
    /// Joins the ORIGINAL execution task to termination, WITHOUT a caller token, and reports
    /// whether it terminated NORMALLY.
    /// </summary>
    /// <remarks>
    /// A cancellation (including a producer cancelled before its body ever started) and any
    /// exception that escaped the execution's own sanitized handler are OBSERVED here — never
    /// re-raised — so the execution task keeps its evidence for the drain that joins it, and this
    /// report can still run its heartbeat-state cleanup. The <c>false</c> result is what suppresses
    /// a fabricated Complete and leaves the Ready claim unconsumed.
    /// </remarks>
    /// <param name="execution">The ORIGINAL execution task.</param>
    /// <returns><c>true</c> when the execution completed normally; otherwise <c>false</c>.</returns>
    private static async Task<bool> ObserveExecutionAsync(Task execution)
    {
        try
        {
            await execution;
            return true;
        }
        catch (OperationCanceledException)
        {
            // Expected: this is how a cancelled producer unwinds (including a pre-start cancel).
            return false;
        }
        catch (Exception)
        {
            // Observed, never re-raised: the execution task itself carries the evidence.
            return false;
        }
    }

    /// <summary>
    /// Creates the per-assignment askpass helper and the config-repo git seam that OWNS it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE OWNERSHIP-TRANSFER GUARD. The helper directory is created, the script written and
    /// (on a non-Windows platform) chmodded to owner-only inside a single <c>try</c> that also
    /// covers the seam construction. Ownership transfers to the seam — via the idempotent
    /// <c>onDispose</c> delete — ONLY when every one of those steps succeeded. Any exception
    /// leaves <c>helperOwned</c> false and the <c>finally</c> best-effort deletes the captured
    /// directory, so a partially-built helper is never left behind on disk.
    /// </para>
    /// <para>
    /// Construction is SYNCHRONOUS and takes NO cancellation token: there is nothing to await
    /// and no interleaving point at which a token could be observed. The <c>finally</c> covers
    /// exceptions only; a cancellation lands AFTER construction, in the preparation or the
    /// execution phase, where the seam is already owned and disposed by the caller's
    /// <c>using</c>.
    /// </para>
    /// </remarks>
    private ConfigRepoGitOperations CreateConfigRepoSeam(WorkerConfigProvisioner provisioner)
    {
        var helperDir = Path.Combine(
            Path.GetTempPath(), $"copilothive-askpass-{Guid.NewGuid():N}");
        var scriptPath = Path.Combine(helperDir, AskpassScriptName);

        var helperOwned = false;
        try
        {
            CreateAskpassDir(helperDir);
            WriteAskpassScript(scriptPath);

            // The SCRIPT first, then the DIR — exactly two calls, or none at all.
            if (AskpassChmodPlatform?.Invoke() ?? !OperatingSystem.IsWindows())
            {
                ApplyOwnerOnlyMode(scriptPath);
                ApplyOwnerOnlyMode(helperDir);
            }

            var seam = new ConfigRepoGitOperations(
                _configRepoDir,
                provisioner,
                _log,
                () => scriptPath,
                BuildHelperDirCleanup(helperDir));

            helperOwned = true;
            return seam;
        }
        finally
        {
            // ONLY when ownership never transferred — otherwise the seam's onDispose owns it.
            if (!helperOwned)
                TryDeleteHelperDir(helperDir);
        }
    }

    /// <summary>
    /// The seam's <c>onDispose</c>: an IDEMPOTENT, best-effort delete of the helper directory.
    /// The interlocked flag means a repeated disposal (or a disposal racing one) deletes once.
    /// </summary>
    private static Action BuildHelperDirCleanup(string helperDir)
    {
        var deleted = 0;
        return () =>
        {
            if (Interlocked.Exchange(ref deleted, 1) == 0)
                TryDeleteHelperDir(helperDir);
        };
    }

    /// <summary>Best-effort recursive delete of the askpass helper directory; never throws.</summary>
    private static void TryDeleteHelperDir(string helperDir)
    {
        try
        {
            if (Directory.Exists(helperDir))
                Directory.Delete(helperDir, recursive: true);
        }
        catch
        {
            // Swallowed — a leaked temp directory must never fail an assignment.
        }
    }

    private void CreateAskpassDir(string helperDir)
    {
        if (AskpassDirCreate is not null)
            AskpassDirCreate(helperDir);
        else
            Directory.CreateDirectory(helperDir);
    }

    /// <summary>
    /// Writes the fixed helper script as UTF-8 WITHOUT a BOM and with its trailing newline —
    /// the bytes matter, since <c>/bin/sh</c> must see <c>#!</c> as the first two bytes.
    /// </summary>
    private void WriteAskpassScript(string scriptPath)
    {
        if (AskpassScriptWrite is not null)
            AskpassScriptWrite(scriptPath);
        else
            File.WriteAllText(scriptPath, AskpassScriptContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// Applies mode 0700 (owner read/write/execute) to ONE path — the SELECTED chmod action:
    /// the injected <see cref="AskpassChmod"/> when non-null, otherwise the REAL
    /// <see cref="File.SetUnixFileMode(string, UnixFileMode)"/>.
    /// </summary>
    /// <remarks>
    /// There is NO platform special-casing here. <see cref="AskpassChmodPlatform"/> (defaulting
    /// to <c>!OperatingSystem.IsWindows()</c>) is the SINGLE authority: it decides whether the
    /// chmod step runs at all. On Windows the real path therefore never reaches this method,
    /// which is precisely why the unsupported-platform call is safe — a no-op branch inside the
    /// action would instead break the null-to-real selection contract by silently doing nothing.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Interoperability",
        "CA1416:Validate platform compatibility",
        Justification = "Guarded by AskpassChmodPlatform, which defaults to !OperatingSystem.IsWindows(); "
            + "the analyzer cannot see through the delegate. The predicate is the single authority.")]
    private void ApplyOwnerOnlyMode(string path)
    {
        if (AskpassChmod is not null)
        {
            AskpassChmod(path);
            return;
        }

        File.SetUnixFileMode(
            path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// Prepares the config repo for one assignment: the HEALTH PROBE, the clone when no repo is
    /// present, and the UNCONDITIONAL <c>agents/</c> directory creation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>agents/</c> creation is idempotent and runs in EVERY non-cancelled case — after a
    /// healthy probe, after a successful clone AND after a failed one — so the improver's
    /// working directory always exists once this returns normally. An existing corrupt or
    /// non-git target is NOT repaired: the probe reports it, the clone is skipped (the target
    /// exists), and the assignment proceeds with whatever the directory holds.
    /// </para>
    /// <para>
    /// A non-cancellation failure PROPAGATES into the assignment body's generic failure handler;
    /// a cancellation unwinds without the directory guarantee.
    /// </para>
    /// </remarks>
    private async Task PrepareConfigRepoAsync(ConfigRepoGitOperations seam, CancellationToken ct)
    {
        var health = await seam.ProbeAndEnsureRepoHealthyAsync(_configRepoDir, ct);

        if (!health.HasRepo)
        {
            var result = await seam.CloneAsync(_configRepoDir, ct);
            if (result.Success)
            {
                _log.Info("Config repo cloned");
            }
            else
            {
                // The seam's error is already URL-redacted; the log rendering additionally
                // strips control characters so git output can never forge a log line.
                _log.Warn("Config repo clone failed: "
                    + LogSanitizer.SanitizeText(GitUrlRedactor.Redact(result.SanitizedError.Trim())));
            }
        }

        Directory.CreateDirectory(Path.Combine(_configRepoDir, "agents"));
    }

    #endregion

    /// <summary>
    /// THE IMPLICIT-REBINDING FIX: the ONE small adapter an assignment's executor is given for BOTH
    /// its tool-call bridge and its session client, BOUND to the assignment's EXPECTED connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY IT EXISTS. The public <see cref="IToolCallBridge"/> / <see cref="ISessionClient"/> members
    /// of <see cref="WorkerService"/> resolve the CURRENT published connection when they are invoked.
    /// Handing the service itself to an executor therefore leaves every bridge or session call it
    /// makes bound to nothing in particular: a call made after a later connection was published would
    /// silently retarget onto that newer registration, pairing one assignment's work with another
    /// connection's stream, identity or client. This adapter captures the expected connection ONCE at
    /// construction — BEFORE execution starts — and every member simply forwards to the service's
    /// SHARED connection-taking implementation with THAT captured connection. There is no worker-ID
    /// lookup and no fallback to a newer published connection anywhere in it.
    /// </para>
    /// <para>
    /// ONE INSTANCE, TWO SLOTS. The same object implements both interfaces, so the two dependencies an
    /// assignment's executor receives are the same captured binding rather than two independently
    /// resolved ones — they can never disagree about which connection the assignment belongs to.
    /// </para>
    /// <para>
    /// It performs NO buffering, retry, replay or synthesis, and it adds NO policy of its own: a
    /// retired captured connection fails with the EXISTING disconnected error (raised by the checked
    /// access inside the shared implementation, not here), a <c>null</c>/successful session outcome is
    /// passed through verbatim, and the caller's token is forwarded unchanged. It is deliberately NOT
    /// exposed: it is created per assignment inside the message loop and never published.
    /// </para>
    /// </remarks>
    private sealed class ConnectionBoundDependencies(WorkerService service, WorkerConnection connection)
        : IToolCallBridge, ISessionClient
    {
        /// <inheritdoc/>
        public Task<string> RequestClarificationAsync(string taskId, string question, CancellationToken ct) =>
            service.RequestClarificationOnConnectionAsync(connection, taskId, question, ct);

        /// <inheritdoc/>
        public Task ReportProgressAsync(string taskId, string status, string details, CancellationToken ct) =>
            service.ReportProgressOnConnectionAsync(connection, taskId, status, details, ct);

        /// <inheritdoc/>
        public Task ReportNarrativeAsync(string taskId, string narrative, CancellationToken ct) =>
            service.ReportNarrativeOnConnectionAsync(connection, taskId, narrative, ct);

        /// <inheritdoc/>
        public Task<string> GetGoalAsync(string taskId, string goalId, CancellationToken ct) =>
            service.GetGoalOnConnectionAsync(connection, taskId, goalId, ct);

        /// <inheritdoc/>
        public Task<string> RaiseIssueAsync(
            string taskId, string type, string title, string description, string severity, CancellationToken ct) =>
            service.RaiseIssueOnConnectionAsync(connection, taskId, type, title, description, severity, ct);

        /// <inheritdoc/>
        public Task<string?> GetSessionAsync(string sessionId, CancellationToken ct) =>
            service.GetSessionOnConnectionAsync(connection, sessionId, ct);

        /// <inheritdoc/>
        public Task SaveSessionAsync(string sessionId, string sessionJson, CancellationToken ct) =>
            service.SaveSessionOnConnectionAsync(connection, sessionId, sessionJson, ct);
    }

    #region IToolCallBridge

    // PUBLIC FACADES OVER THE SHARED, CONNECTION-TAKING IMPLEMENTATION. These members resolve the
    // CURRENT published connection exactly as before (the checked <see cref="RequireConnection"/>,
    // whose disconnected error surfaces on the returned task because these methods are async) and
    // then delegate to the ONE implementation below with that connection. The connection-bound
    // adapter that assignments actually receive calls the SAME implementation, but with the
    // connection it CAPTURED, so the two surfaces can never drift apart.

    /// <inheritdoc/>
    public async Task<string> RequestClarificationAsync(string taskId, string question, CancellationToken ct) =>
        await RequestClarificationOnConnectionAsync(RequireConnection(), taskId, question, ct);

    /// <inheritdoc/>
    public async Task ReportProgressAsync(string taskId, string status, string details, CancellationToken ct)
    {
        // FIRE-AND-FORGET: no response is awaited, so this takes NO response-lifetime check and
        // registers nothing. It still snapshots ONE connection and writes on it.
        var connection = RequireConnection();
        await ReportProgressOnConnectionAsync(connection, taskId, status, details, ct);
    }

    /// <inheritdoc/>
    public async Task ReportNarrativeAsync(string taskId, string narrative, CancellationToken ct)
    {
        // FIRE-AND-FORGET: see ReportProgressAsync.
        var connection = RequireConnection();
        await ReportNarrativeOnConnectionAsync(connection, taskId, narrative, ct);
    }

    /// <inheritdoc/>
    public async Task<string> GetGoalAsync(string taskId, string goalId, CancellationToken ct) =>
        await GetGoalOnConnectionAsync(RequireConnection(), taskId, goalId, ct);

    /// <inheritdoc/>
    public async Task<string> RaiseIssueAsync(string taskId, string type, string title, string description, string severity, CancellationToken ct) =>
        await RaiseIssueOnConnectionAsync(RequireConnection(), taskId, type, title, description, severity, ct);

    /// <summary>A fresh request ID for one tool call.</summary>
    private static string NewRequestId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// THE <c>request_clarification</c> IMPLEMENTATION, bound to the connection PASSED IN. The wire
    /// tool name and the serialized arguments live HERE, so every caller — the public facade above
    /// and the connection-bound adapter — sends byte-identical requests.
    /// </summary>
    private Task<string> RequestClarificationOnConnectionAsync(
        WorkerConnection connection, string taskId, string question, CancellationToken ct) =>
        SendResponseBearingToolCallAsync(
            connection, taskId, "request_clarification",
            System.Text.Json.JsonSerializer.Serialize(new { question }), ct);

    /// <summary>
    /// THE <c>get_goal</c> IMPLEMENTATION, bound to the connection PASSED IN.
    /// </summary>
    private Task<string> GetGoalOnConnectionAsync(
        WorkerConnection connection, string taskId, string goalId, CancellationToken ct) =>
        SendResponseBearingToolCallAsync(
            connection, taskId, "get_goal",
            System.Text.Json.JsonSerializer.Serialize(new { goal_id = goalId }), ct);

    /// <summary>
    /// THE <c>raise_issue</c> IMPLEMENTATION, bound to the connection PASSED IN.
    /// </summary>
    private Task<string> RaiseIssueOnConnectionAsync(
        WorkerConnection connection, string taskId, string type, string title, string description,
        string severity, CancellationToken ct) =>
        SendResponseBearingToolCallAsync(
            connection, taskId, "raise_issue",
            System.Text.Json.JsonSerializer.Serialize(new { type, title, description, severity }), ct);

    /// <summary>
    /// THE <c>report_progress</c> IMPLEMENTATION, bound to the connection PASSED IN. FIRE-AND-FORGET:
    /// it registers NO response and awaits only its write.
    /// </summary>
    private Task ReportProgressOnConnectionAsync(
        WorkerConnection connection, string taskId, string status, string details, CancellationToken ct) =>
        SendToolCallRequest(
            connection, NewRequestId(), taskId, "report_progress",
            System.Text.Json.JsonSerializer.Serialize(new { status, details }), ct);

    /// <summary>
    /// THE <c>report_narrative</c> IMPLEMENTATION, bound to the connection PASSED IN. FIRE-AND-FORGET:
    /// see <see cref="ReportProgressOnConnectionAsync"/>.
    /// </summary>
    private Task ReportNarrativeOnConnectionAsync(
        WorkerConnection connection, string taskId, string narrative, CancellationToken ct) =>
        SendToolCallRequest(
            connection, NewRequestId(), taskId, "report_narrative",
            System.Text.Json.JsonSerializer.Serialize(new { narrative }), ct);

    /// <summary>
    /// THE ONE RESPONSE-BEARING BRIDGE HELPER: REGISTERS the pending response ON THE GIVEN
    /// connection, sends the request on it, awaits the genuine server response and converts it to
    /// the bridge's string result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE CONNECTION IS A PARAMETER, NEVER A FIELD READ. Every step — registration, the request
    /// write, the caller-cancellation registration and the removal — uses the SAME object the
    /// caller handed in, so registration and sending can never re-read a different
    /// <c>CurrentConnection</c>. The public facade passes the connection it just resolved through
    /// <see cref="RequireConnection"/>; the connection-bound adapter passes the connection its
    /// assignment CAPTURED, which is what makes a retained assignment's calls unable to retarget to
    /// a newer published connection.
    /// </para>
    /// <para>
    /// The not-connected error is unchanged: a facade caller observes it from
    /// <see cref="RequireConnection"/> before the first await. Registration itself is
    /// checked against the connection's response lifetime, so a call whose response could no longer
    /// be delivered fails with the EXISTING disconnected error BEFORE anything is written.
    /// </para>
    /// <para>
    /// TERMINATION IS FIRST-TERMINAL-WINNER. A genuine response, the caller's own cancellation and
    /// the connection's response closure all race to settle the ONE pending task; the first to
    /// settle wins and later attempts are no-ops. There is no universal precedence claim between
    /// them. No automatic resend is ever performed: if a response is lost, the REMOTE OUTCOME IS
    /// UNKNOWN, and re-sending could duplicate a remote effect.
    /// </para>
    /// </remarks>
    /// <param name="connection">The expected connection this call belongs to.</param>
    /// <param name="taskId">The task this tool call belongs to.</param>
    /// <param name="toolName">The wire tool name.</param>
    /// <param name="argsJson">The serialized tool arguments.</param>
    /// <param name="ct">The CALLER's token, which cancels this request.</param>
    private async Task<string> SendResponseBearingToolCallAsync(
        WorkerConnection connection, string taskId, string toolName, string argsJson, CancellationToken ct)
    {
        var requestId = NewRequestId();

        // REGISTER FIRST, on the connection PASSED IN. A closed response lifetime (or a retired
        // connection) throws the EXISTING disconnected error here, before any transport is attempted.
        var responseTask = connection.RegisterToolResponse(requestId);

        // CALLER CANCELLATION still cancels THIS request, using THIS caller's token, and removes the
        // entry so a cancelled wait never lingers.
        using var reg = ct.Register(() => connection.CancelToolResponse(requestId, ct));

        try
        {
            await SendToolCallRequest(
                connection, requestId, taskId, toolName, argsJson, ct, responseBearing: true);

            var response = await responseTask;
            return response.Success ? response.ResultJson : $"Error: {response.Error}";
        }
        catch
        {
            // A losing send can leave the pending task faulted or cancelled. Observe it — a faulted
            // task must never go unobserved — WITHOUT masking this send's ORIGINAL error, which is
            // what propagates.
            ObserveAbandonedResponse(responseTask);
            throw;
        }
        finally
        {
            // Settled entries are removed rather than accumulated. A no-op when a terminal path has
            // already dropped it.
            connection.RemoveToolResponse(requestId);
        }
    }

    /// <summary>
    /// Observes a response task abandoned by a failing send so its exception (from a racing
    /// connection teardown) is never unobserved. Deliberately does NOT await, rethrow or otherwise
    /// wait: the send's own ORIGINAL exception is the authoritative outcome and must not be masked.
    /// </summary>
    /// <remarks>
    /// The continuation is UNCONDITIONAL rather than fault-only: the fault can arrive at any moment
    /// (or never, if no teardown races the send), and reading <see cref="Task.Exception"/> is safe on
    /// every terminal state — it is <c>null</c> for a successful or cancelled task. Nothing here
    /// extends the call's lifetime or delays it.
    /// </remarks>
    private static void ObserveAbandonedResponse(Task responseTask) =>
        _ = responseTask.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// Builds and sends ONE tool-call request ON THE GIVEN CONNECTION through the shared send
    /// boundary.
    /// </summary>
    /// <remarks>
    /// The connection is passed in EXPLICITLY — the caller snapshots it before it waits, so a send
    /// that parks behind another writer still targets the connection it was intended for, and
    /// registration and sending can never re-read a different <c>CurrentConnection</c>. When
    /// <paramref name="responseBearing"/> is set, the send goes through the connection's
    /// RESPONSE-BEARING WRITE BOUNDARY once the send gate has been acquired (see
    /// <see cref="SendAsync"/>), which decides openness and initiates the write indivisibly —
    /// restricted to response-bearing sends, so Complete, Ready, progress/narrative and unary paths
    /// are unaffected.
    /// </remarks>
    private Task SendToolCallRequest(
        WorkerConnection connection,
        string requestId,
        string taskId,
        string toolName,
        string argsJson,
        CancellationToken ct,
        bool responseBearing = false)
    {
        var message = new WorkerMessage
        {
            WorkerId = connection.AssignedId,
            ToolRequest = new ToolCallRequest
            {
                RequestId = requestId,
                TaskId = taskId,
                ToolName = toolName,
                ArgumentsJson = argsJson,
            },
        };

        return SendAsync(connection, message, ct, responseBearing);
    }

    #endregion

    #region Session management

    // PUBLIC FACADES OVER THE SHARED, CONNECTION-TAKING IMPLEMENTATION — see the same note on the
    // IToolCallBridge region above. Both facades resolve the CURRENT published connection with the
    // checked <see cref="RequireConnection"/> exactly as before and delegate with it; the
    // connection-bound adapter calls the SAME helpers with the connection it CAPTURED.

    /// <summary>
    /// Retrieves a persisted session from the orchestrator for the given session ID.
    /// Uses the gRPC channel directly (not the bidirectional stream).
    /// </summary>
    /// <param name="sessionId">The session identifier in format "goalId:roleName".</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The session JSON if found, or <c>null</c> if no session exists for the given ID.
    /// </returns>
    /// <remarks>
    /// The connection is resolved ONCE and its client used for the whole call, so the RPC can never
    /// straddle two registrations. Checked access rejects a retired connection before the RPC starts;
    /// an RPC already under way keeps its captured client, token and outcome.
    /// </remarks>
    public async Task<string?> GetSessionAsync(string sessionId, CancellationToken ct) =>
        await GetSessionOnConnectionAsync(RequireConnection(), sessionId, ct);

    /// <summary>
    /// Persists a session to the orchestrator for the given session ID.
    /// Uses the gRPC channel directly (not the bidirectional stream).
    /// </summary>
    /// <param name="sessionId">The session identifier in format "goalId:roleName".</param>
    /// <param name="sessionJson">The serialised session JSON to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>Resolve-once and checked access exactly as in <see cref="GetSessionAsync"/>.</remarks>
    public async Task SaveSessionAsync(string sessionId, string sessionJson, CancellationToken ct) =>
        await SaveSessionOnConnectionAsync(RequireConnection(), sessionId, sessionJson, ct);

    /// <summary>
    /// THE session-load IMPLEMENTATION, bound to the connection PASSED IN. Checked access is taken on
    /// that connection's <c>Client</c> BEFORE the RPC is issued, so a retired connection fails with
    /// the EXISTING disconnected error without starting transport; the found/<c>null</c> mapping and
    /// the caller's token forwarding are unchanged.
    /// </summary>
    private async Task<string?> GetSessionOnConnectionAsync(
        WorkerConnection connection, string sessionId, CancellationToken ct)
    {
        var client = connection.EnsureUsable().Client;

        var response = await client.GetSessionAsync(
            new GetSessionRequest { SessionId = sessionId },
            cancellationToken: ct);

        return response.Found ? response.SessionJson : null;
    }

    /// <summary>
    /// THE session-save IMPLEMENTATION, bound to the connection PASSED IN — checked access and token
    /// forwarding exactly as in <see cref="GetSessionOnConnectionAsync"/>.
    /// </summary>
    private async Task SaveSessionOnConnectionAsync(
        WorkerConnection connection, string sessionId, string sessionJson, CancellationToken ct)
    {
        var client = connection.EnsureUsable().Client;

        await client.SaveSessionAsync(
            new SaveSessionRequest { SessionId = sessionId, SessionJson = sessionJson },
            cancellationToken: ct);
    }

    #endregion

    /// <summary>
    /// THE SINGLE OUTBOUND SEND BOUNDARY. Every WorkStream request write — terminal
    /// <c>Complete</c>, tool requests and every <c>Ready</c> — goes through here, so at most one
    /// underlying <c>RequestStream.WriteAsync</c> is outstanding at a time.
    /// </summary>
    /// <param name="connection">
    /// The connection captured by the CALLER before it waits. Passing the whole connection
    /// explicitly (rather than re-reading the published field after the wait) guarantees a waiting
    /// send is never rerouted onto a different connection — and that its identity and its writer
    /// always come from the same snapshot.
    /// </param>
    /// <param name="message">The fully built message — construction happens outside the gate.</param>
    /// <param name="ct">
    /// Honoured BOTH while waiting for the gate and during the underlying write. A pre-cancelled
    /// or cancelled-while-waiting call writes nothing, cancels no other sender, and releases no
    /// permit it never acquired.
    /// </param>
    /// <param name="responseBearing">
    /// <c>true</c> ONLY for a send whose caller is waiting on this connection for a response (the
    /// <c>request_clarification</c> / <c>get_goal</c> / <c>raise_issue</c> bridge calls). Such a send
    /// goes through the connection's RESPONSE-BEARING WRITE BOUNDARY, which decides the response
    /// lifetime is open and INITIATES the write under one lock, so a request that was queued while
    /// the lifetime was open can never begin a write whose response could no longer be delivered to
    /// it. Complete, Ready, progress/narrative sends and unary sessions pass <c>false</c> and are
    /// entirely unaffected.
    /// </param>
    /// <remarks>
    /// The permit is released in <c>finally</c> AFTER a successful acquisition only — including
    /// when the underlying write fails synchronously or asynchronously, or is cancelled — so the
    /// next caller stays usable whenever the stream itself is still usable. Failures propagate
    /// unchanged to the caller's existing logging/handling: a failed write is never swallowed,
    /// retried or converted into success. The gate is NOT held across a <c>ToolCallResponse</c>
    /// await or an assignment drain, and unary RPCs (heartbeat, session, provisioning) plus the
    /// response reader stay entirely outside it.
    /// <para>
    /// RETIREMENT IS CHECKED AFTER THE GATE IS ACQUIRED. A send that was already queued when the
    /// connection retired therefore cannot write on it (nor on a replacement): it fails with the
    /// existing disconnected error instead. A RESPONSE-BEARING send then performs its openness
    /// decision and its write INITIATION as one indivisible step inside the connection's boundary,
    /// so the lifetime cannot close in between; the resulting write is still AWAITED here, inside
    /// this gate, exactly as every other write is. The write itself still consumes the captured
    /// connection, so a permitted write keeps its captured stream, token and outcome, and an
    /// in-flight write task is never abandoned.
    /// </para>
    /// </remarks>
    private async Task SendAsync(
        WorkerConnection connection, WorkerMessage message, CancellationToken ct, bool responseBearing = false)
    {
        await _sendGate.WaitAsync(ct);
        try
        {
            connection.EnsureUsable();

            // RESPONSE-BEARING ONLY: the openness decision and the write INITIATION happen together
            // inside the connection's boundary, so a closed response lifetime can never let a queued
            // request begin transport. The returned in-flight write is awaited here — inside the
            // shared gate, never inside the connection's lock — so it is never abandoned.
            await (responseBearing
                ? connection.WriteResponseBearingAsync(message, ct)
                : connection.Stream.RequestStream.WriteAsync(message, ct));
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Writes one <c>WorkerReady</c> on the given connection, carrying that connection's own
    /// identity — never a separately mutable ID value.
    /// </summary>
    private Task SendWorkerReady(WorkerConnection connection, CancellationToken ct) =>
        SendAsync(connection, new WorkerMessage
        {
            WorkerId = connection.AssignedId,
            Ready = new WorkerReady(),
        }, ct);

    /// <summary>
    /// The heartbeat loop for ONE published connection. Cadence is UNCHANGED: it still waits for
    /// <see cref="HeartbeatInterval"/> before every tick, and there is deliberately NO immediate
    /// first heartbeat.
    /// </summary>
    private async Task RunHeartbeatAsync(WorkerConnection connection, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);

        while (await timer.WaitForNextTickAsync(ct))
            await SendHeartbeatAsync(connection, ct);
    }

    /// <summary>
    /// ONE heartbeat tick on the given connection: snapshots the connection's identity and the
    /// worker's current task state AT the tick, so the emitted request belongs to the registration
    /// this loop was started for and reflects the state at that instant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the loop body factored out verbatim (a TEST SEAM in shape only: the loop above is its
    /// sole production caller), so a focused test can drive exactly one tick without waiting for a
    /// real interval. The failure contract is UNCHANGED — a non-cancellation fault is logged in
    /// sanitized form and the loop continues to the next tick.
    /// </para>
    /// <para>
    /// CHECKED ACCESS comes FIRST, before any transport: a retired connection issues no heartbeat
    /// RPC at all. That rejection lands on the existing sanitized log path — a heartbeat is a
    /// periodic best-effort signal, so it keeps the existing swallow-and-retry contract rather than
    /// propagating. A tick that already passed the check keeps its captured client, token and
    /// outcome.
    /// </para>
    /// </remarks>
    private async Task SendHeartbeatAsync(WorkerConnection connection, CancellationToken ct)
    {
        try
        {
            var client = connection.EnsureUsable().Client;
            var taskId = _currentTaskId;
            await client.HeartbeatAsync(new HeartbeatRequest
            {
                WorkerId = connection.AssignedId,
                Busy = taskId is not null,
                CurrentTaskId = taskId ?? "",
                CurrentRole = _currentRole ?? "",
                ContextUsagePercent = taskId is not null
                    ? _agentRunner.GetContextUsagePercent()
                    : 0,
            }, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sanitized: heartbeats retry across the gRPC boundary, whose status details can
            // echo request configuration back to the worker.
            //
            // GUARDED: a heartbeat fault is a best-effort diagnostic, so a degraded stderr (a
            // redirected/closed writer, a throwing test seam) must not be able to fault the
            // heartbeat loop and turn a swallow-and-retry tick into a teardown-time join failure.
            try
            {
                Console.Error.WriteLine($"[Worker] Heartbeat failed [{SafeExceptionLog.Describe(ex)}]");
            }
            catch
            {
                // The tick's swallow-and-retry contract is what matters, not the diagnostic.
            }
        }
    }

    /// <summary>
    /// FINALLY disposes this service's single agent runner. The runner is fallible to dispose, and
    /// its failure PROPAGATES unchanged (see the remarks), but the service's lifecycle state is
    /// claimed ONE-WAY first, so the whole operation is idempotent and a throwing first disposal is
    /// never retried.
    /// </summary>
    /// <remarks>
    /// <para>
    /// RUN-GUARD FIRST. Disposal while a run is IN PROGRESS is refused with
    /// <see cref="InvalidOperationException"/> WITHOUT changing state and WITHOUT touching the
    /// runner: the caller must cancel the run and await it first. Claiming is an atomic
    /// Idle → Disposed transition performed BEFORE the fallible runner disposal, so a repeat call is
    /// a NO-OP even when the FIRST disposal threw, and the ORIGINAL exception the first call surfaced
    /// is preserved rather than re-raised or replaced by a second runner interaction.
    /// </para>
    /// <para>
    /// There is deliberately NO blocking drain and no wait for in-flight work: refusing while Running
    /// is the whole contract, and it keeps disposal from silently racing a live run.
    /// </para>
    /// <para>
    /// Runner disposal is deliberately fallible and PROPAGATES. <c>GetAwaiter().GetResult()</c>
    /// rethrows the original exception rather than wrapping it in an AggregateException the
    /// way Wait() does, so the sanitized handler in Program.cs classifies the real fault.
    /// Program.cs runs this inside its try, so a throwing disposal is redacted, never dumped
    /// raw by the runtime.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">A run is in progress; cancel and await it first.</exception>
    public void Dispose()
    {
        while (true)
        {
            var observed = Volatile.Read(ref _lifecycleState);

            // A run owns the runner: fail fast, change NO state, and do NOT dispose underneath it.
            if (observed == LifecycleRunning)
            {
                throw new InvalidOperationException(
                    "Cannot dispose the worker service while a run is in progress — cancel and await "
                    + "the run first.");
            }

            // Already CLAIMED (the first call won, whether or not its runner disposal threw): a
            // repeat is a no-op, so the original failure is never re-raised or replaced.
            if (observed == LifecycleDisposed)
                return;

            if (Interlocked.CompareExchange(ref _lifecycleState, LifecycleDisposed, LifecycleIdle) == LifecycleIdle)
                break;
        }

        // CLAIMED BEFORE this fallible step: a throwing disposal therefore still leaves the service
        // terminally disposed, exactly like a successful one.
        _agentRunner.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
