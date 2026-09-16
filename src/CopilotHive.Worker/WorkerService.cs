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
    private void UnpublishConnection(WorkerConnection expected) =>
        Interlocked.CompareExchange(ref _connection, null, expected);

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
        var connection = new WorkerConnection(
            assignedId, client, stream, provisionerOverride: TestProvisioner,
            provisioningEnvironment: _provisioningEnvironment);

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
    /// Tracks one assignment's identity, its in-flight EXECUTION, its separately owned
    /// connection-bound REPORTING, its cancellation scope, its Ready claim and its terminal result.
    /// </summary>
    /// <remarks>
    /// TWO OWNED TASKS, ONE OWNER. Execution is the work itself (provisioning, preparation, the
    /// executor and the retention of its result); reporting is everything that has to travel over
    /// THIS connection's stream (the Complete write and the single Ready attempt). They are
    /// separated so a held, failed or cancelled transport write can no longer keep the execution
    /// task itself running. The owner keeps BOTH ORIGINAL tasks, and every ownership transition
    /// (replacement, matching cancel, teardown) joins BOTH before the CTS is disposed and the slot
    /// is cleared — neither task is ever abandoned.
    /// </remarks>
    private sealed class ActiveAssignment(
        string taskId,
        Task execution,
        Task reporting,
        CancellationTokenSource cts,
        ReadyClaim readyClaim,
        TerminalResultHolder terminalResult)
    {
        /// <summary>
        /// The assignment's task ID. A <c>CancelTask</c> is only applied when its
        /// <c>TaskId</c> matches this value, so a LATE cancel for an already-finished task can
        /// never abort the assignment that replaced it, nor consume its Ready claim.
        /// </summary>
        public string TaskId { get; } = taskId;

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
        /// holds only THIS task.
        /// </summary>
        public Task Reporting { get; } = reporting;

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
    /// Ownership transition — INSTALL. Called after BOTH original tasks have been OBTAINED (the
    /// execution from <c>Task.Run</c> and the reporting from its own async invocation), so only
    /// fully constructed state (task ID, both tasks, CTS, Ready claim, result holder) is ever
    /// published; those tasks capture the assignment-local values, not this slot.
    /// </summary>
    private void InstallActiveAssignment(ActiveAssignment assignment) => _activeAssignment = assignment;

    /// <summary>
    /// Ownership transition — DRAIN-THEN-CLEAR on REPLACEMENT. Awaits BOTH the retained execution
    /// and its reporting WITHOUT cancelling them (their Ready already flowed, so they are finished
    /// or finishing), disposes the CTS, and only then clears the slot.
    /// </summary>
    /// <remarks>
    /// The clear happens ONLY after the drain returned — i.e. after BOTH original tasks joined and
    /// the CTS disposal was attempted — so a replacement never installs its own assignment, nor
    /// resets the shared runner, while the original execution or its report is still running. With
    /// <c>cancelFirst: false</c> there is no cancellation callback to fail, so nothing is deferred
    /// here.
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
    /// BOTH original tasks, disposes the CTS, and only then clears the slot. Returns the drained
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
    /// The ownership slot is cleared here — after BOTH original tasks joined and the CTS disposal
    /// was attempted — so a deferred cancellation failure can never leave the slot occupied for a
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
    /// RETIREMENT ORDER. The loop's <c>finally</c> ends this connection's tool-response waits FIRST,
    /// then drains the retained assignment, and only THEN retires the connection. Ending responses
    /// before the drain matters: a bridge call parked on a response whose loop has ended can never
    /// be released by a response, so a wait bound to an independent live token would otherwise hold
    /// the drain — and retirement — off forever. Draining before retiring means an EOF or reader
    /// failure with a LIVE token still permits the draining body's single Ready attempt (that Ready
    /// is written while the connection is still usable). Retiring before the caller disposes the
    /// stream means a new operation can never start transport on a connection whose stream is about
    /// to go away.
    /// </remarks>
    private async Task ProcessMessagesAsync(WorkerConnection connection, CancellationToken ct)
    {
        var stream = connection.Stream;

        // The loop's PRIMARY failure (a reader fault, a cancelled read, a handler failure). It is
        // recorded before the cleanup block, so a secondary cancellation-cleanup failure reported by
        // the teardown drain can never REPLACE an already-propagating primary error.
        Exception? primaryFailure = null;

        try
        {
            await foreach (var message in ReadMessages(stream.ResponseStream, ct))
            {
                switch (message.PayloadCase)
                {
                    case OrchestratorMessage.PayloadOneofCase.Assignment:
                        // Task-assignment ownership is serialized: only one task may ever own the
                        // mutable runner and its LLM client, so the previous one is drained BEFORE
                        // the runner is reset. Single-flight Ready (above) ensures the orchestrator
                        // never has two assignments in flight against this worker at once, so this
                        // await cannot starve a previous task of its ToolResponse.
                        if (_activeAssignment is not null)
                        {
                            // Await BOTH original tasks WITHOUT cancelling: single-flight Ready
                            // means a new assignment only follows a Ready this assignment already
                            // emitted, so its execution and its report are finished or finishing.
                            // Cancelling here would abort work that the orchestrator still expects
                            // to complete.
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
                                        _agentRunner, this, sessionClient: this, configRepoDir: _configRepoDir);
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
                                        _agentRunner, this, gitOperations: null, sessionClient: this,
                                        configRepoDir: _configRepoDir, configRepoSeam: seam);
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
                            execution, domainTask, connection, terminalResult, readyClaim, ct);

                        // BOTH ORIGINAL TASKS are obtained BEFORE the fully constructed owner is
                        // published, so the slot never exposes a half-built assignment.
                        InstallActiveAssignment(
                            new ActiveAssignment(
                                domainTask.TaskId, execution, reporting, taskCts, readyClaim, terminalResult));
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

                    case OrchestratorMessage.PayloadOneofCase.None:
                        break;
                }
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
            var teardownDrainFailure = _activeAssignment is not null
                ? await DrainRetainedForTeardownAsync()
                : null;

            _currentTaskId = null;
            _currentRole = null;

            // RETIRE ACCESS only AFTER the drain above. A report draining behind an EOF or a reader
            // failure with a LIVE token therefore still got its single Ready attempt; from here on,
            // any NEW operation on this connection fails disconnected instead of starting transport.
            connection.Retire();

            // ERROR PRECEDENCE. With a primary loop failure already propagating (a reader fault, a
            // cancelled read, a handler failure), that primary is what surfaces and the secondary
            // cancellation-cleanup failure is reported through guarded sanitized logging ONLY — a
            // logger failure can never replace the real failure. Without a primary, the deferred
            // failure propagates now, AFTER the clear, the retirement and BOTH joins above.
            PropagateOrReport(
                teardownDrainFailure, primaryFailure, TaskCancellationFailedMessage);
        }
    }

    /// <summary>
    /// Waits for BOTH of an assignment's ORIGINAL tasks — its EXECUTION and its connection-bound
    /// REPORTING — to finish, optionally cancelling the assignment first, then disposes its
    /// <see cref="CancellationTokenSource"/>. Never throws for cancellation: the whole point is to
    /// reach a quiescent state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// BOTH JOINS ALWAYS HAPPEN, in that order, WITHOUT a caller token — a caller token can never
    /// make either join vacuous, and neither task is ever abandoned. An EXECUTION fault cannot skip
    /// the REPORTING join (it is captured, exactly as before) and neither can a throwing
    /// cancellation callback: that failure is CAPTURED and returned to the caller, to be re-raised
    /// <em>after</em> both joins and the disposal below, rather than skipping them.
    /// </para>
    /// <para>
    /// Ordinary cancellation tolerance and the sanitized treatment of task faults are unchanged for
    /// both tasks: an <see cref="OperationCanceledException"/> is expected, and any other fault is
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
        // CAPTURE FIRST: a callback failure must not bypass either join or the disposal below.
        var deferredCancellationFailure = cancelFirst
            ? await CaptureCancellationFailureAsync(assignment.Cts)
            : null;

        // The EXECUTION first, then the REPORTING that awaits it — each captured, so neither a
        // fault nor a guarded diagnostic can skip what follows.
        ReportIfPresent(
            await CaptureJoinFailureAsync(assignment.Execution), DrainObservedFaultMessage);
        ReportIfPresent(
            await CaptureJoinFailureAsync(assignment.Reporting), DrainObservedFaultMessage);

        // Disposal is attempted AFTER both joins and runs even when a deferred cancellation failure
        // is waiting to propagate — the deferred failure surfaces only once resources are released.
        assignment.Cts.Dispose();

        return deferredCancellationFailure;
    }

    /// <summary>The sanitized report message for a fault observed while draining an assignment.</summary>
    private const string DrainObservedFaultMessage = "Task drain observed a fault";

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
    /// consumes the already-retained terminal result for the Complete mapping, write and
    /// completion diagnostic, clears the heartbeat's task state, and makes the assignment's SINGLE
    /// <c>WorkerReady</c> attempt through the shared claim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ASSIGNMENT-LOCAL INPUTS ONLY. Every value it needs — the ORIGINAL execution task, the domain
    /// task, the EXPECTED connection, the holder and the Ready claim — is passed in by the
    /// assignment handler that created them. Nothing is discovered through the ownership slot and
    /// nothing is re-read from the published connection, so a report can never be retargeted to a
    /// later registration.
    /// </para>
    /// <para>
    /// THE PRODUCER JOIN IS UNCONDITIONAL. The execution task is awaited directly (never through a
    /// cancellation-skippable continuation), so a producer that was CANCELLED BEFORE ITS BODY EVER
    /// STARTED is still observed here. A producer that did not reach termination normally — a
    /// cancelled start, or an exception that escaped its own sanitized handler (for example a
    /// throwing diagnostic) — leaves the holder empty: no Complete is fabricated, and the Ready
    /// claim stays UNCONSUMED so a matching cancel can still emit the single Ready. That is exactly
    /// the pre-split policy, in which such an escape also skipped the claim. The producer's own
    /// failure evidence stays on the execution task and is reported by the drain that joins it;
    /// reporting neither re-raises nor duplicates it.
    /// </para>
    /// <para>
    /// TRANSPORT FAILURES BELONG HERE. A failed or cancelled Complete write keeps the existing
    /// sanitized handling and never overwrites, truncates or discards the retained result; a failed
    /// Ready CONSUMES the claim and is never retried. Both are faults of THIS task only — the
    /// execution task has long since terminated.
    /// </para>
    /// </remarks>
    /// <param name="execution">The ORIGINAL execution task this report belongs to.</param>
    /// <param name="task">The domain task (its ID is used for the completion diagnostic).</param>
    /// <param name="connection">
    /// The EXPECTED connection this assignment belongs to. The Complete and Ready writes consume
    /// THIS object's stream and identity, so a report can never be written on a different
    /// registration than the one the assignment arrived on.
    /// </param>
    /// <param name="terminalResult">The assignment-local holder carrying the retained result.</param>
    /// <param name="readyClaim">The assignment's shared single-flight Ready claim.</param>
    /// <param name="streamToken">The STREAM's token, used for the Complete and Ready writes.</param>
    private async Task ReportAssignmentAsync(
        Task execution,
        WorkTask task,
        WorkerConnection connection,
        TerminalResultHolder terminalResult,
        ReadyClaim readyClaim,
        CancellationToken streamToken)
    {
        var executionTerminatedNormally = false;
        try
        {
            executionTerminatedNormally = await ObserveExecutionAsync(execution);

            // CONSUME what is already retained. An empty holder (a producer that failed before a
            // result existed) reports nothing at all rather than fabricating a completion.
            if (executionTerminatedNormally && terminalResult.Result is { } result)
            {
                await SendAsync(connection, new WorkerMessage
                {
                    WorkerId = connection.AssignedId,
                    Complete = GrpcMapper.ToGrpc(result),
                }, streamToken);

                _log.Info($"Task {task.TaskId} completed ({result.Status})");
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
            // THE EXISTING LOGICAL POINT: after completion reporting, before the single Ready.
            _currentTaskId = null;
            _currentRole = null;
        }

        // Single-flight: only emitted if the cancel handler has not already claimed Ready for this
        // same assignment. A failed write consumes the claim and is never retried.
        if (executionTerminatedNormally && readyClaim.TryClaim())
            await SendWorkerReady(connection, streamToken);
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

    #region IToolCallBridge

    /// <inheritdoc/>
    public Task<string> RequestClarificationAsync(string taskId, string question, CancellationToken ct) =>
        SendResponseBearingToolCallAsync(
            taskId, "request_clarification",
            System.Text.Json.JsonSerializer.Serialize(new { question }), ct);

    /// <inheritdoc/>
    public async Task ReportProgressAsync(string taskId, string status, string details, CancellationToken ct)
    {
        // FIRE-AND-FORGET: no response is awaited, so this takes NO response-lifetime check and
        // registers nothing. It still snapshots ONE connection and writes on it.
        var connection = RequireConnection();
        await SendToolCallRequest(
            connection, NewRequestId(), taskId, "report_progress",
            System.Text.Json.JsonSerializer.Serialize(new { status, details }), ct);
    }

    /// <inheritdoc/>
    public async Task ReportNarrativeAsync(string taskId, string narrative, CancellationToken ct)
    {
        // FIRE-AND-FORGET: see ReportProgressAsync.
        var connection = RequireConnection();
        await SendToolCallRequest(
            connection, NewRequestId(), taskId, "report_narrative",
            System.Text.Json.JsonSerializer.Serialize(new { narrative }), ct);
    }

    /// <inheritdoc/>
    public Task<string> GetGoalAsync(string taskId, string goalId, CancellationToken ct) =>
        SendResponseBearingToolCallAsync(
            taskId, "get_goal",
            System.Text.Json.JsonSerializer.Serialize(new { goal_id = goalId }), ct);

    /// <inheritdoc/>
    public Task<string> RaiseIssueAsync(string taskId, string type, string title, string description, string severity, CancellationToken ct) =>
        SendResponseBearingToolCallAsync(
            taskId, "raise_issue",
            System.Text.Json.JsonSerializer.Serialize(new { type, title, description, severity }), ct);

    /// <summary>A fresh request ID for one tool call.</summary>
    private static string NewRequestId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// THE ONE RESPONSE-BEARING BRIDGE HELPER: snapshots ONE connection, REGISTERS the pending
    /// response ON THAT SAME connection, sends the request on it, awaits the genuine server
    /// response and converts it to the bridge's string result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SNAPSHOT ONCE, BEFORE REGISTRATION. The connection is captured before the pending entry
    /// exists, and every later step — registration, the request write, the removal — uses that SAME
    /// object, so registration and sending can never re-read a different
    /// <c>CurrentConnection</c>. Both the identity and the writer come from that one snapshot, so
    /// they cannot disagree.
    /// </para>
    /// <para>
    /// The not-connected error is unchanged and still raised before any wait. Registration itself is
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
    /// <param name="taskId">The task this tool call belongs to.</param>
    /// <param name="toolName">The wire tool name.</param>
    /// <param name="argsJson">The serialized tool arguments.</param>
    /// <param name="ct">The CALLER's token, which cancels this request.</param>
    private async Task<string> SendResponseBearingToolCallAsync(
        string taskId, string toolName, string argsJson, CancellationToken ct)
    {
        var connection = RequireConnection();
        var requestId = NewRequestId();

        // REGISTER FIRST, on the SAME snapshot. A closed response lifetime (or a retired connection)
        // throws the EXISTING disconnected error here, before any transport is attempted.
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
    /// The connection is snapshotted ONCE and its client used for the whole call, so the RPC can
    /// never straddle two registrations. Checked access rejects a retired connection before the RPC
    /// starts; an RPC already under way keeps its captured client, token and outcome.
    /// </remarks>
    public async Task<string?> GetSessionAsync(string sessionId, CancellationToken ct)
    {
        var client = RequireConnection().Client;

        var response = await client.GetSessionAsync(
            new GetSessionRequest { SessionId = sessionId },
            cancellationToken: ct);

        return response.Found ? response.SessionJson : null;
    }

    /// <summary>
    /// Persists a session to the orchestrator for the given session ID.
    /// Uses the gRPC channel directly (not the bidirectional stream).
    /// </summary>
    /// <param name="sessionId">The session identifier in format "goalId:roleName".</param>
    /// <param name="sessionJson">The serialised session JSON to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>Snapshot-once and checked access exactly as in <see cref="GetSessionAsync"/>.</remarks>
    public async Task SaveSessionAsync(string sessionId, string sessionJson, CancellationToken ct)
    {
        var client = RequireConnection().Client;

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

    private static async IAsyncEnumerable<T> ReadMessages<T>(
        IAsyncStreamReader<T> reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (await reader.MoveNext(ct))
        {
            yield return reader.Current;
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
