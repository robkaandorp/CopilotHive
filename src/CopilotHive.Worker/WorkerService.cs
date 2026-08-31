using System.Collections.Concurrent;
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

    // Pending tool calls awaiting orchestrator responses, keyed by request_id
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ToolCallResponse>> _pendingToolCalls = new();

    // Current task state — read by heartbeat, written by message loop
    private volatile string? _currentTaskId;
    private volatile string? _currentRole;

    // The gRPC stream reference, set during WorkStream processing
    private AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>? _stream;
    private string? _assignedId;

    // The gRPC client, set after successful registration — used by session RPCs
    private HiveOrchestrator.HiveOrchestratorClient? _client;

    /// <summary>
    /// The provisioner created in <see cref="RunAsync"/> after an ACCEPTED registration. It is
    /// the SAME instance handed to the agent runner, so the config-repo seam resolves its URL
    /// and credential from exactly the state the runner's own provisioning fetch produced.
    /// Remains <c>null</c> until registration succeeds (and for direct message-loop tests that
    /// never run <see cref="RunAsync"/>), which selects the LEGACY, seam-free path.
    /// </summary>
    private WorkerConfigProvisioner? _provisioner;

    /// <summary>
    /// TEST SEAM — overrides the <see cref="RunAsync"/>-created provisioner. When BOTH this and
    /// <see cref="_provisioner"/> are <c>null</c> the per-assignment config-repo preparation is
    /// SKIPPED entirely (no probe, no clone, no askpass helper, no seam) and the executor is
    /// built with the legacy public constructor.
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

    /// <summary>
    /// Runs the full worker lifecycle: connects to Copilot, registers with the orchestrator,
    /// opens a bidirectional gRPC stream, and processes task assignments until cancelled.
    /// </summary>
    /// <param name="ct">Cancellation token that stops the worker.</param>
    public async Task RunAsync(CancellationToken ct)
    {
        // Prepare the agent runner. This creates NO LLM client: worker containers hold no LLM
        // credentials of their own, so the client is created lazily on the first prompt, after
        // the orchestrator has provisioned credentials.
        _log.Info("Preparing SharpCoder agent engine...");
        await _agentRunner.ConnectAsync(ct);

        // Enable HTTP/2 over plaintext (required for gRPC without TLS in Docker network)
        using var channel = GrpcChannel.ForAddress(orchestratorUrl, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
            }
        });
        var client = new HiveOrchestrator.HiveOrchestratorClient(channel);
        _client = client;

        // 1. Register
        var registerRequest = new RegisterRequest
        {
            WorkerId = workerId,
        };
        registerRequest.Capabilities.AddRange(capabilities);

        var registerResponse = await client.RegisterAsync(registerRequest, cancellationToken: ct);

        if (!registerResponse.Accepted)
        {
            _log.Error("Registration rejected by orchestrator.");
            return;
        }

        _assignedId = string.IsNullOrEmpty(registerResponse.AssignedWorkerId)
            ? workerId
            : registerResponse.AssignedWorkerId;

        _log.Info($"Registered as {_assignedId} (orchestrator v{registerResponse.OrchestratorVersion})");

        // Registration happens BEFORE the operator may have completed OAuth sign-in, so the
        // provisioning fetch is deliberately NOT performed here. It runs immediately before every
        // first LLM client creation, by which time a token committed after sign-in is visible.
        var provisioner = new WorkerConfigProvisioner(
            _assignedId,
            (request, token) => client.GetWorkerConfigAsync(request, cancellationToken: token).ResponseAsync);
        _agentRunner.SetConfigProvisioner(provisioner.EnsureProvisionedAsync);

        // The SAME instance backs the per-assignment config-repo seam, so the seam's URL and
        // credential resolution always reflects the runner's provisioning state.
        _provisioner = provisioner;

        // 2. Start heartbeat background task
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = RunHeartbeatAsync(client, _assignedId, heartbeatCts.Token);

        try
        {
            // 3. Open bidirectional work stream
            using var stream = client.WorkStream(cancellationToken: ct);
            _stream = stream;

            // 4. Send WorkerReady
            await SendWorkerReady(stream, _assignedId, ct);

            // 5. Main message loop
            await ProcessMessagesAsync(stream, _assignedId, ct);
        }
        finally
        {
            _stream = null;
            await heartbeatCts.CancelAsync();
            try { await heartbeatTask; } catch (OperationCanceledException) { }
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

    /// <summary>Tracks one assignment's identity, in-flight execution, cancellation scope and Ready claim.</summary>
    private sealed class ActiveAssignment(
        string taskId, Task execution, CancellationTokenSource cts, ReadyClaim readyClaim)
    {
        /// <summary>
        /// The assignment's task ID. A <c>CancelTask</c> is only applied when its
        /// <c>TaskId</c> matches this value, so a LATE cancel for an already-finished task can
        /// never abort the assignment that replaced it, nor consume its Ready claim.
        /// </summary>
        public string TaskId { get; } = taskId;

        /// <summary>The running task body.</summary>
        public Task Execution { get; } = execution;

        /// <summary>Cancellation source scoped to this assignment.</summary>
        public CancellationTokenSource Cts { get; } = cts;

        /// <summary>The shared single-flight Ready claim for this assignment.</summary>
        public ReadyClaim Ready { get; } = readyClaim;
    }

    private async Task ProcessMessagesAsync(
        AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> stream,
        string assignedId,
        CancellationToken ct)
    {
        ActiveAssignment? active = null;

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
                        if (active is not null)
                        {
                            // Await WITHOUT cancelling: single-flight Ready means a new assignment
                            // only follows a Ready this assignment already emitted, so the body is
                            // finished or finishing. Cancelling here would abort work that the
                            // orchestrator still expects to complete.
                            await DrainAssignmentAsync(active, cancelFirst: false);
                            active = null;
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

                        // The Ready claim and the CTS are created BEFORE the body starts, so the
                        // body never observes a half-initialised assignment. (Capturing a variable
                        // assigned after Task.Run would race with the body's first statement.)
                        var readyClaim = new ReadyClaim();
                        var bodyCts = taskCts;

                        // Run task execution concurrently so message loop can process
                        // ToolCallResponse messages from the orchestrator during execution
                        var execution = Task.Run(async () =>
                        {
                            try
                            {
                                // STEP 1 — provisioner selection. A null provisioner (no
                                // RunAsync registration, no test override) SKIPS the whole
                                // config-repo preparation and keeps the LEGACY executor.
                                var provisioner = TestProvisioner ?? _provisioner;
                                if (provisioner is null)
                                {
                                    var legacyExecutor = new TaskExecutor(
                                        _agentRunner, this, sessionClient: this, configRepoDir: _configRepoDir);
                                    await ExecuteAndReportAsync(
                                        legacyExecutor, domainTask, stream, assignedId, bodyCts.Token, ct);
                                }
                                else
                                {
                                    // STEP 2 — the ONE eager provisioning call. Everything below
                                    // depends on the provisioner's config-repo accessors, which
                                    // throw until the environment snapshot has been taken.
                                    await provisioner.EnsureProvisionedAsync(taskModel, bodyCts.Token);

                                    // STEPS 3-4 — the askpass helper and the seam that owns it.
                                    // The seam is a per-assignment LOCAL: WorkerService owns it,
                                    // and this `using` encloses the executor's whole lifetime.
                                    using var seam = CreateConfigRepoSeam(provisioner);

                                    // STEP 5 — probe / clone / agents directory, BEFORE the
                                    // executor exists, let alone runs.
                                    await PrepareConfigRepoAsync(seam, bodyCts.Token);

                                    // STEP 6 — the executor is constructed LAST and receives the
                                    // caller-owned seam; it never disposes it.
                                    var executor = new TaskExecutor(
                                        _agentRunner, this, gitOperations: null, sessionClient: this,
                                        configRepoDir: _configRepoDir, configRepoSeam: seam);
                                    await ExecuteAndReportAsync(
                                        executor, domainTask, stream, assignedId, bodyCts.Token, ct);
                                }
                            }
                            catch (OperationCanceledException) { }
                            catch (Exception ex)
                            {
                                // Sanitized: task execution wraps the LLM HTTP boundary, whose error
                                // payloads can echo provisioned configuration (tokens, API keys).
                                _log.Error($"Task execution failed [{SafeExceptionLog.Describe(ex)}]");
                            }
                            finally
                            {
                                _currentTaskId = null;
                                _currentRole = null;
                            }

                            // Single-flight: only emitted if the cancel handler has not already
                            // claimed Ready for this same assignment.
                            if (readyClaim.TryClaim())
                                await SendWorkerReady(stream, assignedId, ct);
                        }, ct);

                        active = new ActiveAssignment(domainTask.TaskId, execution, taskCts, readyClaim);
                        break;

                    case OrchestratorMessage.PayloadOneofCase.Cancel:
                        var cancel = message.Cancel;
                        _log.Info($"Cancel requested for task {cancel.TaskId}: {cancel.Reason}");

                        if (active is not null)
                        {
                            // Correlate by task ID. A LATE cancel for an already-completed task A
                            // must NOT abort the assignment B that replaced it, and must not
                            // consume B's single-flight Ready claim — doing so would strand B and
                            // desynchronise the orchestrator's view of this worker.
                            if (!string.Equals(active.TaskId, cancel.TaskId, StringComparison.Ordinal))
                            {
                                _log.Info(
                                    $"Ignoring stale cancel for task {cancel.TaskId} — the active task is " +
                                    $"{active.TaskId}, which keeps running.");
                                break;
                            }

                            var cancelled = active;
                            await DrainAssignmentAsync(cancelled, cancelFirst: true);
                            active = null;

                            _currentTaskId = null;
                            _currentRole = null;

                            // Single-flight: the drained body normally claims Ready itself. Only
                            // emit here if it did not (e.g. it was cancelled before reaching the
                            // claim), so a cancel never produces a second dequeue.
                            if (cancelled.Ready.TryClaim())
                                await SendWorkerReady(stream, assignedId, ct);
                        }
                        else
                        {
                            // Nothing in flight — the worker is already idle, so a single Ready
                            // keeps the orchestrator's view accurate.
                            _currentTaskId = null;
                            _currentRole = null;
                            await SendWorkerReady(stream, assignedId, ct);
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
                        if (_pendingToolCalls.TryRemove(response.RequestId, out var tcs))
                        {
                            tcs.TrySetResult(response);
                        }
                        else
                        {
                            // Expected for fire-and-forget tools like report_progress
                            _log.Debug($"Received ToolCallResponse for untracked request: {response.RequestId}");
                        }
                        break;

                    case OrchestratorMessage.PayloadOneofCase.None:
                        break;
                }
            }
        }
        finally
        {
            // Stream shutdown must not leave a task running: Program disposes the runner right
            // after this returns, and a still-running turn holds the client lifecycle lease.
            // Cancel then drain so the runner is quiescent before disposal.
            if (active is not null)
            {
                await DrainAssignmentAsync(active, cancelFirst: true);
                active = null;
            }

            _currentTaskId = null;
            _currentRole = null;
        }
    }

    /// <summary>
    /// Waits for an assignment's body to finish, optionally cancelling it first, then disposes its
    /// <see cref="CancellationTokenSource"/>. Never throws for cancellation — the whole point is
    /// to reach a quiescent state.
    /// </summary>
    /// <param name="assignment">The assignment to drain.</param>
    /// <param name="cancelFirst">
    /// <c>true</c> to request cancellation before awaiting (cancel handling and stream teardown);
    /// <c>false</c> to simply await an assignment that is already finishing.
    /// </param>
    private async Task DrainAssignmentAsync(ActiveAssignment assignment, bool cancelFirst)
    {
        if (cancelFirst)
        {
            try
            {
                await assignment.Cts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed by an earlier drain — nothing to cancel.
            }
        }

        try
        {
            await assignment.Execution;
        }
        catch (OperationCanceledException)
        {
            // Expected: this is how a cancelled body unwinds.
        }
        catch (Exception ex)
        {
            // The body already sanitizes and logs its own failures; this is a last-resort guard so
            // draining never propagates a task fault into the message loop or teardown path.
            _log.Error($"Task drain observed a fault [{SafeExceptionLog.Describe(ex)}]");
        }

        assignment.Cts.Dispose();
    }

    #region Assignment execution and config-repo preparation

    /// <summary>
    /// Runs one assignment through an executor and reports its result upstream. Shared by BOTH
    /// dispatch forms (the legacy, seam-free executor and the seam-carrying one) so the two can
    /// never drift apart in what they write or log.
    /// </summary>
    /// <param name="executor">The executor to run — already fully constructed.</param>
    /// <param name="task">The domain task.</param>
    /// <param name="stream">The bidirectional work stream.</param>
    /// <param name="assignedId">This worker's orchestrator-assigned identifier.</param>
    /// <param name="bodyToken">The ASSIGNMENT's token, which cancels the execution itself.</param>
    /// <param name="streamToken">The STREAM's token, used for the completion write.</param>
    private async Task ExecuteAndReportAsync(
        TaskExecutor executor,
        WorkTask task,
        AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> stream,
        string assignedId,
        CancellationToken bodyToken,
        CancellationToken streamToken)
    {
        var result = await executor.ExecuteAsync(task, bodyToken);

        await stream.RequestStream.WriteAsync(new WorkerMessage
        {
            WorkerId = assignedId,
            Complete = GrpcMapper.ToGrpc(result),
        }, streamToken);

        _log.Info($"Task {task.TaskId} completed ({result.Status})");
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
    public async Task<string> RequestClarificationAsync(string taskId, string question, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ToolCallResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingToolCalls[requestId] = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled());

        try
        {
            await SendToolCallRequest(requestId, taskId, "request_clarification",
                System.Text.Json.JsonSerializer.Serialize(new { question }), ct);

            var response = await tcs.Task;
            return response.Success ? response.ResultJson : $"Error: {response.Error}";
        }
        finally
        {
            _pendingToolCalls.TryRemove(requestId, out _);
        }
    }

    /// <inheritdoc/>
    public async Task ReportProgressAsync(string taskId, string status, string details, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        await SendToolCallRequest(requestId, taskId, "report_progress",
            System.Text.Json.JsonSerializer.Serialize(new { status, details }), ct);
    }

    /// <inheritdoc/>
    public async Task ReportNarrativeAsync(string taskId, string narrative, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        await SendToolCallRequest(requestId, taskId, "report_narrative",
            System.Text.Json.JsonSerializer.Serialize(new { narrative }), ct);
    }

    /// <inheritdoc/>
    public async Task<string> GetGoalAsync(string taskId, string goalId, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ToolCallResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingToolCalls[requestId] = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled());

        try
        {
            await SendToolCallRequest(requestId, taskId, "get_goal",
                System.Text.Json.JsonSerializer.Serialize(new { goal_id = goalId }), ct);

            var response = await tcs.Task;
            return response.Success ? response.ResultJson : $"Error: {response.Error}";
        }
        finally
        {
            _pendingToolCalls.TryRemove(requestId, out _);
        }
    }

    /// <inheritdoc/>
    public async Task<string> RaiseIssueAsync(string taskId, string type, string title, string description, string severity, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ToolCallResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingToolCalls[requestId] = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled());

        try
        {
            await SendToolCallRequest(requestId, taskId, "raise_issue",
                System.Text.Json.JsonSerializer.Serialize(new { type, title, description, severity }), ct);

            var response = await tcs.Task;
            return response.Success ? response.ResultJson : $"Error: {response.Error}";
        }
        finally
        {
            _pendingToolCalls.TryRemove(requestId, out _);
        }
    }

    private async Task SendToolCallRequest(string requestId, string taskId, string toolName, string argsJson, CancellationToken ct)
    {
        if (_stream is null || _assignedId is null)
            throw new InvalidOperationException("Not connected to orchestrator");

        await _stream.RequestStream.WriteAsync(new WorkerMessage
        {
            WorkerId = _assignedId,
            ToolRequest = new ToolCallRequest
            {
                RequestId = requestId,
                TaskId = taskId,
                ToolName = toolName,
                ArgumentsJson = argsJson,
            },
        }, ct);
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
    public async Task<string?> GetSessionAsync(string sessionId, CancellationToken ct)
    {
        if (_client is null)
            throw new InvalidOperationException("Not connected to orchestrator");

        var response = await _client.GetSessionAsync(
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
    public async Task SaveSessionAsync(string sessionId, string sessionJson, CancellationToken ct)
    {
        if (_client is null)
            throw new InvalidOperationException("Not connected to orchestrator");

        await _client.SaveSessionAsync(
            new SaveSessionRequest { SessionId = sessionId, SessionJson = sessionJson },
            cancellationToken: ct);
    }

    #endregion

    private static async Task SendWorkerReady(
        AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> stream,
        string assignedId,
        CancellationToken ct)
    {
        await stream.RequestStream.WriteAsync(new WorkerMessage
        {
            WorkerId = assignedId,
            Ready = new WorkerReady(),
        }, ct);
    }

    private async Task RunHeartbeatAsync(
        HiveOrchestrator.HiveOrchestratorClient client,
        string assignedId,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);

        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var taskId = _currentTaskId;
                await client.HeartbeatAsync(new HeartbeatRequest
                {
                    WorkerId = assignedId,
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
                Console.Error.WriteLine($"[Worker] Heartbeat failed [{SafeExceptionLog.Describe(ex)}]");
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

    /// <inheritdoc/>
    public void Dispose()
    {
        // Dispose the agent runner (which disposes the IChatClient) so each
        // retry gets a fresh connection without leaking the previous one.
        //
        // Runner disposal is deliberately fallible and PROPAGATES. GetAwaiter().GetResult()
        // rethrows the original exception rather than wrapping it in an AggregateException the
        // way Wait() does, so the sanitized handler in Program.cs classifies the real fault.
        // Program.cs runs this inside its try, so a throwing disposal is redacted, never dumped
        // raw by the runtime.
        _agentRunner.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
