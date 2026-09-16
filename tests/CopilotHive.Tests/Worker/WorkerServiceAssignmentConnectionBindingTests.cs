using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;

using Grpc.Core;
using Grpc.Net.Client;

using Microsoft.Extensions.AI;

using System.Reflection;
using System.Text.Json;

using SharpCoder;

using GrpcWorkerRole = CopilotHive.Shared.Grpc.WorkerRole;

using RecordingToolRequestStream =
    CopilotHive.Tests.Worker.WorkerConnectionToolCallLifetimeTests.RecordingToolRequestStream;
using BridgeCapturingRunner =
    CopilotHive.Tests.Worker.WorkerConnectionToolCallLifetimeTests.BridgeCapturingRunner;
using RecordingSessionInvoker =
    CopilotHive.Tests.Worker.WorkerConnectionToolCallLifetimeTests.RecordingSessionInvoker;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// THE PROVISIONED EXECUTOR BRANCH's assignment-dependency binding.
/// </summary>
/// <remarks>
/// <para>
/// <c>WorkerService</c> builds its <c>TaskExecutor</c> in TWO places: the LEGACY, seam-free branch
/// (taken when the connection carries NO provisioner) and the PROVISIONED branch (taken when it
/// does — the one that eagerly provisions, builds the askpass helper and the config-repo seam, and
/// then constructs the executor around that seam). The sibling cases in
/// <see cref="WorkerConnectionToolCallLifetimeTests"/> build their connections WITHOUT a
/// provisioner, so they only ever reach the legacy branch: the provisioned branch could regress to
/// handing the reusable service itself to the executor and every one of those tests would stay
/// green.
/// </para>
/// <para>
/// This fixture closes that hole. It drives the REAL <c>ProcessMessagesAsync</c> assignment setup
/// on a connection that CARRIES a provisioner — with the existing
/// <see cref="ProvisionerHarness"/> and the <c>GitOperations.ProcessRunner</c> seam making the
/// eager <c>EnsureProvisionedAsync</c> and the config-repo preparation succeed — captures the
/// dependency the PROVISIONED branch actually installed on the executor (through the runner's own
/// <c>SetToolBridge</c> seam, with no production instrumentation), moves the service's publication
/// to a second connection BEFORE the first operation, and proves every operation still travels over
/// the captured connection with ZERO traffic on the newly published one.
/// </para>
/// <para>
/// Every gate is a TCS or a counted write — no sleeps, no timing-based ordering — and every await
/// is bounded purely as a failure guard. The <c>GitOperations.ProcessRunner</c> seam is static, so
/// this fixture joins the non-parallel console collection the other loop-driving fixtures use.
/// </para>
/// </remarks>
[Collection("ConsoleOutput")]
public sealed class WorkerServiceAssignmentConnectionBindingTests : IDisposable
{
    /// <summary>An ELIGIBLE (HTTPS github.com:443) config repo URL, matching the wiring fixture.</summary>
    private const string EligibleUrl = "https://github.com/org/config-repo.git";

    /// <summary>Generous failsafe bound; never an ordering device.</summary>
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(15);

    /// <summary>A process-wide client channel: no RPC is ever issued through it.</summary>
    private static readonly GrpcChannel Channel = GrpcChannel.ForAddress("http://localhost:9999");

    private readonly string _root;

    public WorkerServiceAssignmentConnectionBindingTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "copilothive-binding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ConfigRepoDir);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>The config repo directory for one test — a child of the per-test root.</summary>
    private string ConfigRepoDir => Path.Combine(_root, "config-repo");

    /// <summary>
    /// THE PROVISIONED BRANCH BINDS ITS DEPENDENCY TOO. A real assignment runs on connection A
    /// whose PROVISIONER selects the seam path (proved by the eager provisioning fetch and the
    /// config-repo health probe, neither of which the legacy branch performs). The dependency the
    /// PROVISIONED <c>TaskExecutor</c> construction installed is captured through the runner's
    /// <c>SetToolBridge</c> seam; the service's publication then moves to connection B BEFORE the
    /// first bridge operation. Every bridge operation — and the unary session pair reached by
    /// casting that SAME captured object to <see cref="ISessionClient"/>, which is exactly how
    /// production hands one object to both executor slots — must still travel over A, carrying A's
    /// worker ID and the assignment's task ID, while B records ZERO writes and ZERO session RPCs.
    /// <para>
    /// REMOVAL-PROOFNESS: if the PROVISIONED branch passed the reusable service as either
    /// dependency, every call would resolve the CURRENT published connection after the publication
    /// change, land on B's writer/invoker, and fail the exact-A and zero-B assertions by name. The
    /// provisioning-fetch and probe assertions make the branch selection itself non-vacuous, so
    /// this can never silently degrade into a second legacy-branch test.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ProvisionedBranchAssignmentDependency_BoundToCapturedConnection_StaysOnAWithZeroBTraffic()
    {
        const string assignedIdA = "worker-provisioned-a";
        const string taskId = "task-provisioned";

        var launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        // The CONNECTION's own provisioner — NOT the TestProvisioner seam — is what selects the
        // provisioned branch here, exactly as production does after a real registration.
        var provisionerHarness = new ProvisionerHarness(configRepoUrl: EligibleUrl, ghToken: "ghp_test");

        var runner = new BridgeCapturingRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);

        var requestsA = new RecordingToolRequestStream();
        var responsesA = new ChannelResponseReader();
        var invokerA = new CountingSessionInvoker();
        var requestsB = new RecordingToolRequestStream();
        var responsesB = new ChannelResponseReader();
        var invokerB = new CountingSessionInvoker();

        // A CARRIES the provisioner: this is the branch selector under test.
        var connectionA = BuildConnection(
            assignedIdA, requestsA, responsesA, invokerA, provisionerHarness.Provisioner);
        var connectionB = BuildConnection(
            "worker-provisioned-b", requestsB, responsesB, invokerB, provisionerHarness.Provisioner);
        service.PublishConnection(connectionA);

        var loop = InvokeLoop(service, connectionA, TestContext.Current.CancellationToken);
        Task<IToolCallBridge>? captureWait = null;
        try
        {
            responsesA.Push(Assignment(taskId));

            // The dependency the PROVISIONED construction installed — the real adapter instance,
            // not a hand-built stand-in.
            captureWait = runner.Captured.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var bridge = await captureWait;

            // ONE object serves BOTH executor slots in production, so the captured bridge is also
            // the session client. A failure here means the two slots were given different objects.
            var sessions = Assert.IsAssignableFrom<ISessionClient>(bridge);

            await runner.PromptStarted(taskId)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // BRANCH EVIDENCE: the PROVISIONED path really ran. The legacy branch performs no
            // eager provisioning fetch, launches no config-repo git command and creates no
            // agents/ directory — so all three would fail if this had fallen back to it.
            Assert.Equal(1, provisionerHarness.FetchCount);
            Assert.True(
                launcher.Saw("rev-parse", "--is-inside-work-tree"),
                "The provisioned branch must run the config-repo health probe before executing.");
            Assert.True(Directory.Exists(Path.Combine(ConfigRepoDir, "agents")));

            // Nothing has been written yet, so the publication change below genuinely precedes the
            // FIRST operation rather than landing between a register and a send.
            Assert.Equal(0, requestsA.WriteCount);
            Assert.Equal(0, requestsB.WriteCount);

            // ── PUBLICATION MOVES TO B, while the assignment still holds its A-bound dependency. ──
            service.PublishConnection(connectionB);

            // 1. report_progress (fire-and-forget).
            await bridge.ReportProgressAsync(
                    taskId, "prov-status", "prov details", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var progress = await requestsA.WaitForWriteAsync(0, TestContext.Current.CancellationToken);
            Assert.Equal(WorkerMessage.PayloadOneofCase.ToolRequest, progress.PayloadCase);
            Assert.Equal("report_progress", progress.ToolRequest.ToolName);
            Assert.Equal(taskId, progress.ToolRequest.TaskId);
            Assert.Equal(assignedIdA, progress.WorkerId);
            Assert.Equal("""{"status":"prov-status","details":"prov details"}""",
                progress.ToolRequest.ArgumentsJson);

            // 2. report_narrative (fire-and-forget).
            await bridge.ReportNarrativeAsync(
                    taskId, "prov narrative", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var narrative = await requestsA.WaitForWriteAsync(1, TestContext.Current.CancellationToken);
            Assert.Equal("report_narrative", narrative.ToolRequest.ToolName);
            Assert.Equal(taskId, narrative.ToolRequest.TaskId);
            Assert.Equal(assignedIdA, narrative.WorkerId);
            Assert.Equal("""{"narrative":"prov narrative"}""", narrative.ToolRequest.ArgumentsJson);

            // 3. request_clarification (response-bearing) — registered, sent and RESOLVED on A.
            var clarification = bridge.RequestClarificationAsync(
                taskId, "why provisioned?", TestContext.Current.CancellationToken);
            var clarificationWrite = await requestsA.WaitForWriteAsync(
                2, TestContext.Current.CancellationToken);
            Assert.Equal("request_clarification", clarificationWrite.ToolRequest.ToolName);
            Assert.Equal(taskId, clarificationWrite.ToolRequest.TaskId);
            Assert.Equal(assignedIdA, clarificationWrite.WorkerId);
            Assert.Equal("""{"question":"why provisioned?"}""",
                clarificationWrite.ToolRequest.ArgumentsJson);
            Assert.True(connectionA.TryCompleteToolResponse(new ToolCallResponse
            {
                RequestId = clarificationWrite.ToolRequest.RequestId,
                Success = true,
                ResultJson = """{"clarification":"prov-from-A"}""",
            }));
            Assert.Equal(
                """{"clarification":"prov-from-A"}""",
                await clarification.WaitAsync(Failsafe, TestContext.Current.CancellationToken));

            // 4. get_goal (response-bearing).
            var goal = bridge.GetGoalAsync(
                taskId, "goal-provisioned", TestContext.Current.CancellationToken);
            var goalWrite = await requestsA.WaitForWriteAsync(3, TestContext.Current.CancellationToken);
            Assert.Equal("get_goal", goalWrite.ToolRequest.ToolName);
            Assert.Equal(taskId, goalWrite.ToolRequest.TaskId);
            Assert.Equal(assignedIdA, goalWrite.WorkerId);
            Assert.Equal("""{"goal_id":"goal-provisioned"}""", goalWrite.ToolRequest.ArgumentsJson);
            Assert.True(connectionA.TryCompleteToolResponse(new ToolCallResponse
            {
                RequestId = goalWrite.ToolRequest.RequestId,
                Success = true,
                ResultJson = """{"goal":"prov-A-goal"}""",
            }));
            Assert.Equal("""{"goal":"prov-A-goal"}""",
                await goal.WaitAsync(Failsafe, TestContext.Current.CancellationToken));

            // 5. raise_issue (response-bearing, error conversion unchanged).
            var issue = bridge.RaiseIssueAsync(
                taskId, "bug", "prov title", "prov desc", "high", TestContext.Current.CancellationToken);
            var issueWrite = await requestsA.WaitForWriteAsync(4, TestContext.Current.CancellationToken);
            Assert.Equal("raise_issue", issueWrite.ToolRequest.ToolName);
            Assert.Equal(taskId, issueWrite.ToolRequest.TaskId);
            Assert.Equal(assignedIdA, issueWrite.WorkerId);
            Assert.Equal(
                """{"type":"bug","title":"prov title","description":"prov desc","severity":"high"}""",
                issueWrite.ToolRequest.ArgumentsJson);
            Assert.True(connectionA.TryCompleteToolResponse(new ToolCallResponse
            {
                RequestId = issueWrite.ToolRequest.RequestId,
                Success = false,
                Error = "prov refused",
            }));
            Assert.Equal("Error: prov refused",
                await issue.WaitAsync(Failsafe, TestContext.Current.CancellationToken));

            // 6/7. The SESSION slot of the same captured object: both unary RPCs reach A's client.
            var loaded = await sessions
                .GetSessionAsync("goal-provisioned:coder", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Null(loaded); // A's invoker answers Found=false, mapped to null unchanged.
            Assert.Equal(1, invokerA.LoadCount);
            Assert.Equal("goal-provisioned:coder", invokerA.LastLoadSessionId);

            await sessions
                .SaveSessionAsync(
                    "goal-provisioned:coder", """{"turn":7}""", TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, invokerA.SaveCount);
            Assert.Equal("goal-provisioned:coder", invokerA.LastSaveSessionId);
            Assert.Equal("""{"turn":7}""", invokerA.LastSavedJson);

            // EXACTLY the five bridge writes reached A — no resend, no extra traffic.
            Assert.Equal(5, requestsA.WriteCount);

            // ZERO B TRAFFIC: the newly published connection saw no write, no session RPC and no
            // registered response wait.
            Assert.Equal(0, requestsB.WriteCount);
            Assert.Equal(0, invokerB.LoadCount);
            Assert.Equal(0, invokerB.SaveCount);
            Assert.Equal(0, connectionB.PendingToolResponseCount);
        }
        finally
        {
            runner.ReleaseAll();
            connectionA.EndToolResponses();
            connectionB.EndToolResponses();
            responsesA.TryComplete();
            responsesB.TryComplete();
            await JoinAllForCleanupAsync(
                (captureWait, nameof(captureWait)), (loop, nameof(loop)));
        }
    }

    /// <summary>
    /// THE PROVISIONED BRANCH's SESSION-CLIENT SLOT, exercised by the EXECUTOR ITSELF.
    /// <para>
    /// The sibling bridge case above captures the object installed through
    /// <see cref="IAgentRunner.SetToolBridge"/> and casts it to <see cref="ISessionClient"/>, so it
    /// only ever proves the BRIDGE slot: its assignment carries no <c>SessionId</c>, so
    /// <c>TaskExecutor</c> performs no session work at all and the separate <c>sessionClient:</c>
    /// constructor argument stays unused. A regression that wired ONLY that argument back to the
    /// reusable service would leave every assertion there green.
    /// </para>
    /// <para>
    /// This case closes that hole. The assignment CARRIES a <c>SessionId</c>, so the real
    /// <c>TaskExecutor</c> performs the load and the save THROUGH ITS OWN INJECTED
    /// <c>sessionClient</c> — the test never calls <see cref="ISessionClient"/> itself. The
    /// production ordering is what discriminates:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///   The executor's LOAD runs BEFORE the prompt, i.e. while A is still the published
    ///   connection. A <c>sessionClient: this</c> regression would therefore STILL land the load on
    ///   A — the load alone can never detect it, which is exactly why the save matters.
    ///   </description></item>
    ///   <item><description>
    ///   The executor's SAVE runs AFTER the prompt returns, i.e. after publication has already
    ///   moved to B. A <c>sessionClient: this</c> regression resolves the CURRENT published
    ///   connection at that moment and sends the save to <b>B</b>: <c>invokerA.SaveCount</c> stays
    ///   0 and <c>invokerB.SaveCount</c> becomes 1, failing by name. This is THE discriminating
    ///   assertion.
    ///   </description></item>
    /// </list>
    /// <para>
    /// The prompt park is the deterministic window between the two: the executor is provably
    /// mid-execution (its load has already been observed on A) and has not yet reached its save.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ProvisionedBranchExecutorSessionSave_StaysOnCapturedConnection_WhenPublicationMovedToBDuringThePrompt()
    {
        const string assignedIdA = "worker-prov-session-a";
        const string assignedIdB = "worker-prov-session-b";
        const string taskId = "task-prov-session";
        const string sessionId = "goal-prov-session:coder";

        var launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        // The CONNECTION's own provisioner selects the PROVISIONED branch, exactly as production
        // does after a real registration.
        var provisionerHarness = new ProvisionerHarness(configRepoUrl: EligibleUrl, ghToken: "ghp_test");

        // The EXACT payload A serves on the load, and the EXACT payload the executor must save
        // back: it deserializes what it loaded, hands that to the runner, and re-serializes the
        // runner's retained session with the same options — so the saved bytes are fully
        // determined here rather than pattern-matched.
        var loadedJson = JsonSerializer.Serialize(
            AgentSession.Create("prov-session-loaded"), AIJsonUtilities.DefaultOptions);
        var expectedSavedJson = JsonSerializer.Serialize(
            JsonSerializer.Deserialize<AgentSession>(loadedJson, AIJsonUtilities.DefaultOptions),
            AIJsonUtilities.DefaultOptions);

        var invokerA = new RecordingSessionInvoker
        {
            SessionToReturn = new GetSessionResponse { Found = true, SessionJson = loadedJson },
        };
        var invokerB = new RecordingSessionInvoker
        {
            // If a regression sent the save to B, B would answer it happily — the failure must come
            // from the COUNT assertions below, never from B refusing the call.
            SessionToReturn = new GetSessionResponse { Found = false },
        };

        var runner = new BridgeCapturingRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);

        var requestsA = new RecordingToolRequestStream();
        var responsesA = new ChannelResponseReader();
        var requestsB = new RecordingToolRequestStream();
        var responsesB = new ChannelResponseReader();

        var connectionA = BuildConnection(
            assignedIdA, requestsA, responsesA, invokerA, provisionerHarness.Provisioner);
        var connectionB = BuildConnection(
            assignedIdB, requestsB, responsesB, invokerB, provisionerHarness.Provisioner);

        // GENUINELY DISTINCT CONNECTIONS AND CLIENTS: no assertion below can pass because both
        // calls happened to land on one and the same published connection or invoker.
        Assert.NotSame(connectionA, connectionB);
        Assert.NotSame(invokerA, invokerB);
        Assert.NotEqual(connectionA.AssignedId, connectionB.AssignedId);

        service.PublishConnection(connectionA);

        var loop = InvokeLoop(service, connectionA, TestContext.Current.CancellationToken);
        Task<IToolCallBridge>? captureWait = null;
        try
        {
            responsesA.Push(SessionAssignment(taskId, sessionId));

            // The PROVISIONED construction installed its dependency. Capturing it here is only
            // branch evidence — this test never invokes it; the EXECUTOR owns every session call.
            captureWait = runner.Captured.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await captureWait;

            // ── PHASE 1: the executor's own LOAD, while A is STILL the published connection. ──
            // This deliberately cannot discriminate a miswired slot (a `this` regression resolves
            // A here too); it establishes that the executor really does session work and that the
            // session the runner will hold came from A.
            await AwaitExecutorSessionCallOnAAsync(
                invokerA, invokerB, "load", TestContext.Current.CancellationToken);
            Assert.Equal(1, invokerA.LoadCount);
            Assert.Equal(sessionId, invokerA.LastLoadSessionId);
            Assert.Same(connectionA, GetPublishedConnection(service));

            // The prompt has started: the executor is parked strictly BETWEEN its load and its
            // save, which is the window the publication change needs.
            await runner.PromptStarted(taskId)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // BRANCH EVIDENCE: the PROVISIONED path really ran. The legacy branch performs no
            // eager provisioning fetch, launches no config-repo git command and creates no
            // agents/ directory — so all three would fail if this had fallen back to it.
            Assert.Equal(1, provisionerHarness.FetchCount);
            Assert.True(
                launcher.Saw("rev-parse", "--is-inside-work-tree"),
                "The provisioned branch must run the config-repo health probe before executing.");
            Assert.True(Directory.Exists(Path.Combine(ConfigRepoDir, "agents")));

            // THE SAVE HAS NOT HAPPENED YET on either side, so the publication change below
            // genuinely precedes it rather than racing it.
            Assert.Equal(0, invokerA.SaveCount);
            Assert.Equal(0, invokerB.SaveCount);

            // The runner is holding a NON-NULL AgentSession (set by the executor from what it
            // loaded). Verified, not assumed: with a null session TaskExecutor.SaveSessionAsync
            // takes its "no session to save" early return, no save is ever issued, and the
            // discriminating assertion below would be unreachable rather than failing.
            var heldSession = Assert.IsType<AgentSession>(runner.GetSession());
            Assert.Equal("prov-session-loaded", heldSession.SessionId);

            // ── PUBLICATION MOVES TO B, while the assignment still holds its A-bound dependency. ──
            service.PublishConnection(connectionB);
            Assert.Same(connectionB, GetPublishedConnection(service));

            // ── PHASE 2: release the prompt so the EXECUTOR performs its own SAVE — now that B is
            //    the published connection. A `sessionClient: this` regression sends this save to B.
            runner.ReleaseAll();
            await AwaitExecutorSessionCallOnAAsync(
                invokerA, invokerB, "save", TestContext.Current.CancellationToken);

            // The Complete write can only begin after ExecuteAsync returned, so every
            // executor-owned session call has finished by this point: the counts below are a final
            // end state, not an early snapshot that a later duplicate could invalidate.
            var complete = await requestsA.WaitForWriteAsync(0, TestContext.Current.CancellationToken);
            Assert.Equal(WorkerMessage.PayloadOneofCase.Complete, complete.PayloadCase);
            Assert.Equal(assignedIdA, complete.WorkerId);

            // THE DISCRIMINATING ASSERTION: the executor's save — issued after publication moved
            // to B — still reached A's client, with the exact session ID and the exact bytes.
            Assert.Equal(1, invokerA.SaveCount);
            Assert.Equal(sessionId, invokerA.LastSaveSessionId);
            Assert.Equal(expectedSavedJson, invokerA.LastSavedJson);

            // ...and the saved payload really is the session the runner held (not an empty or
            // freshly created one that merely happens to serialize).
            var savedSession = JsonSerializer.Deserialize<AgentSession>(
                invokerA.LastSavedJson!, AIJsonUtilities.DefaultOptions);
            Assert.Equal("prov-session-loaded", savedSession!.SessionId);

            // EXACTLY ONE load and ONE save on A — no duplicate, no retry.
            Assert.Equal(1, invokerA.LoadCount);

            // B — the connection published at save time — saw NO session RPC at all. Under the
            // regression this is where the save would have landed.
            Assert.Equal(0, invokerB.LoadCount);
            Assert.Equal(0, invokerB.SaveCount);
            Assert.Equal(0, requestsB.WriteCount);
        }
        finally
        {
            runner.ReleaseAll();
            connectionA.EndToolResponses();
            connectionB.EndToolResponses();
            responsesA.TryComplete();
            responsesB.TryComplete();
            await JoinAllForCleanupAsync(
                (captureWait, nameof(captureWait)), (loop, nameof(loop)));
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Harness.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds (but does not publish) a connection over the given writer/reader/invoker, CARRYING
    /// the supplied provisioner — which is what selects the PROVISIONED executor branch.
    /// </summary>
    private static WorkerConnection BuildConnection(
        string assignedId,
        IClientStreamWriter<WorkerMessage> requests,
        IAsyncStreamReader<OrchestratorMessage> responses,
        CallInvoker invoker,
        WorkerConfigProvisioner provisioner) =>
        new(assignedId,
            new HiveOrchestrator.HiveOrchestratorClient(invoker),
            new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
                requests, responses,
                _ => Task.FromResult(new Metadata()),
                _ => new Status(StatusCode.OK, string.Empty),
                _ => new Metadata(),
                _ => { },
                null!),
            provisioner,
            includeProductionProvisioner: false);

    private static OrchestratorMessage Assignment(string taskId) => new()
    {
        Assignment = new TaskAssignment
        {
            TaskId = taskId,
            GoalId = "goal-provisioned",
            GoalDescription = "exercise the provisioned branch's bound dependency",
            Prompt = "ask the orchestrator",
            Role = GrpcWorkerRole.Coder,
        },
    };

    /// <summary>
    /// An assignment CARRYING a session ID, which is what makes <c>TaskExecutor</c> perform its own
    /// session load and save through its INJECTED <c>sessionClient</c> — the executor's session
    /// work is gated on <c>!string.IsNullOrEmpty(task.SessionId)</c>, so without this the separate
    /// constructor slot is never exercised at all.
    /// </summary>
    private static OrchestratorMessage SessionAssignment(string taskId, string sessionId) => new()
    {
        Assignment = new TaskAssignment
        {
            TaskId = taskId,
            GoalId = "goal-prov-session",
            GoalDescription = "exercise the provisioned branch's bound session client",
            Prompt = "resume the session",
            Role = GrpcWorkerRole.Coder,
            SessionId = sessionId,
        },
    };

    /// <summary>
    /// The connection the service currently has PUBLISHED (observation only — never mutated), so a
    /// test can prove which connection was live at the load and at the save.
    /// </summary>
    private static WorkerConnection? GetPublishedConnection(WorkerService service) =>
        (WorkerConnection?)typeof(WorkerService)
            .GetField("_connection", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service);

    private static Task InvokeLoop(WorkerService service, WorkerConnection connection, CancellationToken ct) =>
        (Task)typeof(WorkerService)
            .GetMethod("ProcessMessagesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [connection, ct])!;

    /// <summary>
    /// Waits for the EXECUTOR's own session call to reach connection A's client, turning a bound
    /// expiry into a NAMED, diagnostic failure instead of an anonymous timeout.
    /// </summary>
    /// <remarks>
    /// This matters precisely for the regression under test: if the executor's
    /// <c>sessionClient</c> slot were wired to the reusable service, the call would resolve the
    /// CURRENT published connection and land on B (or, if nothing were published, park/throw) — A's
    /// signal would simply never fire. Reporting which invoker actually saw the call names the
    /// defect instead of leaving a bare <see cref="TimeoutException"/> at a line number.
    /// </remarks>
    /// <param name="invokerA">The CAPTURED connection's client — where the call must land.</param>
    /// <param name="invokerB">The newly published connection's client — where a regression lands.</param>
    /// <param name="signal">The session signal to await (<c>"load"</c> or <c>"save"</c>).</param>
    /// <param name="ct">The test's cancellation token.</param>
    private static async Task AwaitExecutorSessionCallOnAAsync(
        RecordingSessionInvoker invokerA,
        RecordingSessionInvoker invokerB,
        string signal,
        CancellationToken ct)
    {
        try
        {
            await invokerA.AwaitAsync(signal, ct);
        }
        catch (TimeoutException ex)
        {
            var landedOnB = string.Equals(signal, "load", StringComparison.Ordinal)
                ? invokerB.LoadCount
                : invokerB.SaveCount;

            throw new TimeoutException(
                $"The executor's own session {signal} never reached connection A's client within "
                    + $"{Failsafe}. B's client saw {landedOnB} {signal}(s). A non-zero count on B "
                    + "means the executor's sessionClient slot resolved the CURRENTLY PUBLISHED "
                    + "connection instead of the assignment's captured one; a zero count on both "
                    + "means the executor issued no session call at all (for example the "
                    + "assignment lost its SessionId, or the runner held no session to save).",
                ex);
        }
    }

    /// <summary>
    /// Teardown join that observes every started producer and NEVER treats a live-task timeout as
    /// successful cleanup: a producer still live on the bound is a NAMED failure.
    /// </summary>
    private static async Task JoinAllForCleanupAsync(params (Task? Producer, string Name)[] producers)
    {
        List<Exception> failures = [];
        foreach (var (producer, name) in producers)
        {
            if (producer is null) continue;

            try
            {
                await producer.WaitAsync(Failsafe);
            }
            catch (TimeoutException ex) when (!producer.IsCompleted)
            {
                failures.Add(new TimeoutException(
                    $"Cleanup failed: producer '{name}' was still live after {Failsafe}.", ex));
            }
            catch (Exception) when (producer.IsCompleted)
            {
                // The ORIGINAL producer is terminal; its scenario outcome was asserted in the body.
            }
        }

        if (failures.Count != 0)
            throw new AggregateException("One or more cleanup producers remained live.", failures);
    }

    /// <summary>A repo that probes HEALTHY, with a matching, credential-free origin.</summary>
    private GitProcessResult HealthyRepoHandler(IReadOnlyList<string> tokens)
    {
        if (Matches(tokens, "rev-parse", "--is-inside-work-tree"))
            return new GitProcessResult(0, "true\n", "");

        if (Matches(tokens, "rev-parse", "--show-toplevel"))
            return new GitProcessResult(0, ConfigRepoDir + "\n", "");

        if (Matches(tokens, "remote", "get-url", "origin"))
            return new GitProcessResult(0, EligibleUrl + "\n", "");

        return new GitProcessResult(0, "", "");
    }

    private static bool Matches(IReadOnlyList<string> tokens, params string[] prefix)
    {
        if (tokens.Count < prefix.Length) return false;

        for (var i = 0; i < prefix.Length; i++)
        {
            if (!string.Equals(tokens[i], prefix[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>
    /// A <see cref="CallInvoker"/> answering ONLY the session RPCs and recording the exact session
    /// ID and JSON each call carried, so a session call landing on the WRONG connection's client is
    /// directly observable as a non-zero count on that client.
    /// </summary>
    private sealed class CountingSessionInvoker : CallInvoker
    {
        private readonly object _gate = new();
        private int _loadCount;
        private int _saveCount;
        private string? _lastLoadSessionId;
        private string? _lastSaveSessionId;
        private string? _lastSavedJson;

        internal int LoadCount { get { lock (_gate) return _loadCount; } }
        internal int SaveCount { get { lock (_gate) return _saveCount; } }
        internal string? LastLoadSessionId { get { lock (_gate) return _lastLoadSessionId; } }
        internal string? LastSaveSessionId { get { lock (_gate) return _lastSaveSessionId; } }
        internal string? LastSavedJson { get { lock (_gate) return _lastSavedJson; } }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException($"Unexpected blocking call {method.FullName}.");

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            object response = method.FullName switch
            {
                "/copilothive.HiveOrchestrator/GetSession" => Load(request),
                "/copilothive.HiveOrchestrator/SaveSession" => Save(request),
                _ => throw new NotSupportedException($"Unexpected unary call {method.FullName}."),
            };

            return new AsyncUnaryCall<TResponse>(
                Task.FromResult((TResponse)response), Task.FromResult(new Metadata()),
                () => new Status(StatusCode.OK, string.Empty), () => new Metadata(), () => { });
        }

        private object Load<TRequest>(TRequest request)
        {
            lock (_gate)
            {
                _loadCount++;
                _lastLoadSessionId = (request as GetSessionRequest)?.SessionId;
            }

            return new GetSessionResponse { Found = false };
        }

        private object Save<TRequest>(TRequest request)
        {
            lock (_gate)
            {
                _saveCount++;
                _lastSaveSessionId = (request as SaveSessionRequest)?.SessionId;
                _lastSavedJson = (request as SaveSessionRequest)?.SessionJson;
            }

            return new SaveSessionResponse { Success = true };
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException($"Unexpected server-streaming call {method.FullName}.");
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException($"Unexpected client-streaming call {method.FullName}.");
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException($"Unexpected duplex call {method.FullName}.");
    }
}
