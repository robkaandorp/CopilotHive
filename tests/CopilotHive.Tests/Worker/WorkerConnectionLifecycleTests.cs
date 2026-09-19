using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;
using CopilotHive.Workers;

using Grpc.Core;

using Microsoft.Extensions.AI;

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using DomainWorkerRole = CopilotHive.Workers.WorkerRole;
using GrpcWorkerRole = CopilotHive.Shared.Grpc.WorkerRole;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// The bounded characterization of the worker's ONE PUBLISHED CONNECTION.
/// <para>
/// The accepted-registration flow is driven through the REAL <see cref="WorkerService.RunAsync"/>
/// using the internal null-default client / call-invoker construction seam (a fake
/// <see cref="CallInvoker"/> plus a fake duplex stream — no live server). The focused tests then pin
/// the connection's own contract (session load/save, provisioning token forwarding, retired-entry
/// rejection, heartbeat argument forwarding) directly against a fake client, and one controlled A/B
/// test proves reference-identity publication.
/// </para>
/// <para>
/// Every gate is a <see cref="TaskCompletionSource"/> or a counted write: there are NO timing sleeps,
/// every await has a bounded failsafe, and every started producer/loop is joined in a <c>finally</c>
/// even on assertion-failure paths. The fake writers implement the CANCELLABLE write overload, so a
/// cancelled write unwinds exactly like a real gRPC stream writer.
/// </para>
/// <para>
/// Heartbeat coverage is deliberately BOUNDED: one tick is driven directly through the factored
/// sender (argument forwarding), and the loop's interval / no-immediate-first-tick behavior is
/// preserved by inspection of the unchanged loop above it. No 30-second tick is ever awaited inside
/// <see cref="WorkerService.RunAsync"/>, and no immediate production heartbeat was added.
/// </para>
/// </summary>
[Collection("ConsoleOutput")]
public sealed class WorkerConnectionLifecycleTests
{
    private const string LocalWorkerId = "worker-local";
    private const string AssignedWorkerId = "worker-assigned";

    /// <summary>A deterministic provider prefix, so credential resolution never depends on the process env.</summary>
    private const string FixtureModel = "copilot/fixture-model";

    /// <summary>Generous failsafe bound; never an ordering device.</summary>
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Bound on teardown's drain-to-fixpoint loop. Each pass joins every newly admitted assignment
    /// execution; exceeding this means the runner kept admitting new work after the recording was
    /// sealed, which is reported as a named failure rather than looped on forever.
    /// </summary>
    private const int MaxTeardownDrainPasses = 8;

    // ══════════════════════════════════════════════════════════════════════════
    // (1) The REAL RunAsync flow.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE ACCEPTED-REGISTRATION FLOW, end to end. Proves connection installation, the assigned-ID
    /// fallback (parameterized over an empty vs non-empty <c>AssignedWorkerId</c>), the initial
    /// Ready, controlled EOF, UNPUBLICATION BEFORE transport disposal, and disconnected session /
    /// provisioning access afterwards — all surfacing the EXISTING disconnected error category.
    /// <para>
    /// The eager per-assignment preparation and the LAZY runner callback are exercised against the
    /// SAME <c>TestProvisioner</c> override, over a fake in-memory environment and fake provisioning
    /// responses — never operator secrets. The config-repo preparation is hermetic: a fake git
    /// launcher reports a HEALTHY repo, so no clone or network access is attempted.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("", LocalWorkerId)]
    [InlineData(AssignedWorkerId, AssignedWorkerId)]
    public async Task RunAsync_AcceptedRegistration_PublishesAssignedIdentity_ThenUnpublishesBeforeDisposal(
        string assignedWorkerIdFromOrchestrator,
        string expectedAssignedId)
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse
        {
            Accepted = true,
            AssignedWorkerId = assignedWorkerIdFromOrchestrator,
            OrchestratorVersion = "test",
        });

        // A REAL provisioner over an in-memory environment with a fake provisioning response —
        // never operator secrets — so the SAME instance can back both provisioning sites.
        var provisionerHarness = new ProvisionerHarness(
            configRepoUrl: "https://github.com/org/config-repo.git", ghToken: "ghp_fixture_token");
        var runner = new ProvisionerCapturingRunner();

        var configRepoDir = CreateTempDir();
        var launcher = new FakeGitLauncher(HealthyRepoHandler(configRepoDir));
        using var processRunner = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var service = BuildService(runner, provisionerHarness.Provisioner, configRepoDir);

        var requests = new RecordingRequestStream();
        var responses = new ChannelResponseReader();

        // Hoisted so the stream's disposal callback can observe the ACTUAL connection object (it is
        // unpublished by then, so reading the field would only ever see null).
        WorkerConnection? connection = null;

        // The REAL run handle, hoisted so the stream's disposal callback and the body below observe
        // the SAME task. Its result is whatever the production RunAsync returned — never a
        // reconstructed or test-generated outcome.
        Task<WorkerRunOutcome>? run = null;

        // Observed INSIDE each write: whether a connection was already published and still usable AT
        // the moment that write was issued. This makes publish-BEFORE-Ready a real ordering claim
        // rather than a post-hoc read.
        var publishedAtEachWrite = new List<bool>();
        requests.OnWrite = _ =>
            publishedAtEachWrite.Add(GetPublishedConnection(service) is { IsRetired: false });

        // The stream's disposal callback runs DURING disposal, so it observes the teardown ordering.
        var retiredAtStreamDisposal = false;
        var unpublishedAtStreamDisposal = false;
        var runCompletedAtStreamDisposal = false;
        var streamDisposals = 0;
        var stream = new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
            requests, responses,
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ =>
            {
                Interlocked.Increment(ref streamDisposals);
                retiredAtStreamDisposal = connection?.IsRetired ?? false;
                unpublishedAtStreamDisposal = GetPublishedConnection(service) is null;
                // The lexical transport disposal is part of the method, so the returned outcome
                // must NOT yet be observable here: the run handle is still uncompleted.
                runCompletedAtStreamDisposal = run?.IsCompleted ?? false;
            },
            null!);

        service.CallInvokerFactory = () => invoker;
        service.WorkStreamFactory = (_, _) => stream;

        using var loopCts = new CancellationTokenSource();

        try
        {
            run = service.RunAsync(loopCts.Token);

            // INSTALLATION + INITIAL READY. The Ready's identity is the orchestrator's ASSIGNED id
            // when it supplied one, and the locally configured worker id otherwise.
            await requests.WaitForWriteCountAsync(1, TestContext.Current.CancellationToken);
            var ready = requests.Writes[0];
            Assert.Equal(WorkerMessage.PayloadOneofCase.Ready, ready.PayloadCase);
            Assert.Equal(expectedAssignedId, ready.WorkerId);

            // PUBLISH-BEFORE-READY, observed AT the initial Ready's own write.
            Assert.True(publishedAtEachWrite[0], "The connection must be published before the initial Ready.");

            connection = Assert.IsType<WorkerConnection>(GetPublishedConnection(service));
            Assert.False(connection.IsRetired);
            Assert.Equal(expectedAssignedId, connection.AssignedId);

            // The registration request carried the LOCALLY configured worker id (the orchestrator
            // assigns its own id in the response).
            Assert.Equal(LocalWorkerId, invoker.LastRegisterWorkerId);

            // ONE provisioner instance backs BOTH provisioning sites, through the CONNECTION's own
            // checked entry point. The runner's LAZY callback is NOT the raw provisioner delegate —
            // it is the connection-owned wrapper — so both sites share one provisioner instance AND
            // one retirement contract. Call-through to that single instance is the evidence.
            Assert.Same(provisionerHarness.Provisioner, connection.Provisioner);
            Assert.NotNull(runner.ConfigProvisioner);
            Assert.NotEqual(connection.Provisioner!.EnsureProvisionedAsync, runner.ConfigProvisioner);

            // CAPTURE the callback WHILE LIVE. RunAsync DETACHES it at quiescence (before releasing
            // the run guard), so a post-run field read would only ever see null. The captured
            // delegate is the very object the production lifecycle installed, and it stays bound to
            // the connection it was created for — never retargeted.
            var lazyCallback = runner.ConfigProvisioner!;

            // The LAZY runner callback reaches that instance and performs one fetch on this
            // fixture's in-memory environment.
            Assert.Equal(0, provisionerHarness.FetchCount);
            await lazyCallback(FixtureModel, TestContext.Current.CancellationToken);
            Assert.Equal(1, provisionerHarness.FetchCount);

            // ...and the EAGER per-assignment site reaches that SAME instance: the count advances
            // again, and it advances on the identical provisioner object.
            responses.Push(Assignment("task-eager"));
            await requests.WaitForReadyCountAsync(2, TestContext.Current.CancellationToken);
            Assert.Equal(2, provisionerHarness.FetchCount);

            // The eager site also ran the config-repo preparation, and the fake launcher saw the
            // health probe — evidence the seam path (not the legacy branch) was taken.
            Assert.True(
                launcher.Saw("rev-parse", "--is-inside-work-tree"),
                "The eager per-assignment preparation must run the health probe on the seam path.");
            Assert.True(Directory.Exists(Path.Combine(configRepoDir, "agents")));

            // A bridge send while the connection is live uses ITS identity and ITS stream.
            await service.ReportProgressAsync(
                "task-eager", "running", "details", TestContext.Current.CancellationToken);
            await requests.WaitForWriteCountAsync(3, TestContext.Current.CancellationToken);
            var progress = requests.Writes[^1];
            Assert.Equal(expectedAssignedId, progress.WorkerId);
            Assert.Equal("report_progress", progress.ToolRequest.ToolName);

            // CONTROLLED EOF ends the loop; the lifecycle then retires and unpublishes.
            responses.TryComplete();
            await run.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // THE REAL RETURNED OUTCOME — captured by awaiting the actual Task<WorkerRunOutcome>,
            // never reconstructed or generated by the fixture. An ACCEPTED registration whose work
            // stream ended with the whole lifecycle teardown completing normally reports observed
            // work-stream completion.
            var outcome = await run;
            Assert.Equal(WorkerRunOutcome.WorkStreamEnded, outcome);

            // ...and that result was NOT observable while the lexical transport disposal ran: the
            // disposal callback is part of the same method, so no outcome existed at that instant.
            Assert.False(
                runCompletedAtStreamDisposal,
                "The run outcome must not be observable before the lexical transport disposal completed.");

            // UNPUBLICATION BEFORE TRANSPORT DISPOSAL — both observed inside the disposal callback.
            Assert.True(connection.IsRetired);
            Assert.Null(GetPublishedConnection(service));
            Assert.True(retiredAtStreamDisposal, "The connection must be retired before its stream is disposed.");
            Assert.True(unpublishedAtStreamDisposal, "The connection must be unpublished before its stream is disposed.");
            Assert.Equal(1, streamDisposals);

            // The retired connection rejects further checked access with the EXISTING error category.
            var retiredFailure = Assert.Throws<InvalidOperationException>(() => connection.EnsureUsable());
            Assert.Equal(WorkerConnection.DisconnectedMessage, retiredFailure.Message);

            // DISCONNECTED SESSION ACCESS afterwards.
            var sessionFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.GetSessionAsync("goal:role", TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, sessionFailure.Message);

            var saveFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.SaveSessionAsync("goal:role", "{}", TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, saveFailure.Message);

            // DISCONNECTED PROVISIONING ACCESS afterwards, through the ACTUAL CAPTURED RUNNER
            // CALLBACK with the OVERRIDE provisioner installed. The override carries its own fetch
            // delegate, so this proves the connection-owned wrapper — not the provisioner's own
            // plumbing — is what rejects a post-teardown attempt: the disconnected error is raised
            // and the override provisioner is never started (its fetch count does not move).
            //
            // The CALLBACK is the one CAPTURED while the connection was live (the lifecycle detaches
            // it at quiescence), and it is still bound to the RETIRED connection — proof the retired
            // callback is never retargeted onto a replacement.
            var overrideFetchesBefore = provisionerHarness.FetchCount;
            var fetchCallsBefore = invoker.WorkerConfigCalls;
            var lazyFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => lazyCallback(FixtureModel, TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, lazyFailure.Message);
            Assert.Equal(overrideFetchesBefore, provisionerHarness.FetchCount);

            // The connection's checked provisioning fetch rejects likewise, starting no transport.
            var provisioningFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => connection.FetchWorkerConfigAsync(
                    new GetWorkerConfigRequest { WorkerId = connection.AssignedId },
                    TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, provisioningFailure.Message);
            Assert.Equal(fetchCallsBefore, invoker.WorkerConfigCalls);

            // DISCONNECTED BRIDGE SEND afterwards.
            var sendFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ReportProgressAsync(
                    "task-after", "running", "after-teardown", TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, sendFailure.Message);
        }
        finally
        {
            var cancellationFailure = await CancelForTeardownAsync(loopCts);
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, cancellationFailure, ("RunAsync", run));
            TryDelete(configRepoDir);
        }
    }

    /// <summary>
    /// A REJECTED registration publishes NOTHING usable: no connection is observable afterwards, no
    /// stream was ever opened, the runner was never handed a provisioner callback, and every access
    /// path still fails with the EXISTING disconnected error.
    /// <para>
    /// The CAPTURED result of the REAL <see cref="WorkerService.RunAsync"/> is
    /// <see cref="WorkerRunOutcome.RegistrationRejected"/> — the value the production method itself
    /// returned, alongside the disconnected-error evidence this test already pinned.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunAsync_RejectedRegistration_PublishesNothingAndOpensNoStream()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse { Accepted = false });
        var runner = new ProvisionerCapturingRunner();
        var streamOpened = 0;
        var service = BuildService(runner, new ProvisionerHarness().Provisioner);

        // The service is disposed by the teardown helper, never by a `using` declaration:
        // disposal must not race ahead of the joins below, and a still-live producer must
        // surface as a loud named failure instead of being disposed out from under.
        try
        {
            service.CallInvokerFactory = () => invoker;
            service.WorkStreamFactory = (_, _) =>
            {
                Interlocked.Increment(ref streamOpened);
                throw new InvalidOperationException("No stream may be opened for a rejected registration.");
            };

            // THE REAL RESULT, captured from the awaited Task<WorkerRunOutcome> — the production
            // method's own returned value, never a fixture-generated one. It says the registration
            // was rejected; nothing was built, streamed or published.
            var outcome = await service.RunAsync(TestContext.Current.CancellationToken);
            Assert.Equal(WorkerRunOutcome.RegistrationRejected, outcome);

            Assert.Null(GetPublishedConnection(service));
            Assert.Equal(0, streamOpened);
            Assert.Null(runner.ConfigProvisioner);
            Assert.Equal(1, invoker.RegisterCalls);
            Assert.Equal(0, invoker.WorkerConfigCalls);

            // Nothing usable was published, so access fails with the EXISTING error category.
            var sessionFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.GetSessionAsync("goal:role", TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, sessionFailure.Message);

            var sendFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ReportProgressAsync("t", "s", "d", TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, sendFailure.Message);
        }
        finally
        {
            await JoinAllForTeardownAsync(service, priorFailure: null);
        }
    }

    /// <summary>
    /// A registration RPC FAILURE is an escaping exception, never
    /// <see cref="WorkerRunOutcome.RegistrationRejected"/>: the run faults with the ORIGINAL fault
    /// identity (the same precedence as before the outcome distinction existed), and the failure
    /// happens where the rejected path's normal return would have been — proving a returned outcome
    /// really does mean "no failure left <see cref="WorkerService.RunAsync"/>".
    /// <para>
    /// REMOVAL PROOF. If the rejection branch ever swallowed the RPC fault (or returned
    /// <see cref="WorkerRunOutcome.RegistrationRejected"/> instead of propagating), the
    /// <see cref="Assert.ThrowsAsync{T}(Func{Task})"/> below fails: no exception escapes the run.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunAsync_RegistrationRpcFails_FaultsInsteadOfReturningOutcome()
    {
        // The registration RPC itself faults: the run must propagate this fault, NOT report any
        // WorkerRunOutcome — in particular never RegistrationRejected.
        var registerFailure = new InvalidOperationException("Registration RPC failed.");
        var invoker = new FaultingRegisterInvoker(registerFailure);
        var runner = new ProvisionerCapturingRunner();
        var streamOpened = 0;
        var service = BuildService(runner, new ProvisionerHarness().Provisioner);

        // The service is disposed by the teardown helper, never by a `using` declaration.
        try
        {
            service.CallInvokerFactory = () => invoker;
            service.WorkStreamFactory = (_, _) =>
            {
                Interlocked.Increment(ref streamOpened);
                throw new InvalidOperationException("No stream may be opened after a failed registration RPC.");
            };

            // THE REAL RUN HANDLE: the awaited result is the production method's own outcome — here
            // it is a fault, with its ORIGINAL identity, not a returned WorkerRunOutcome value.
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.RunAsync(TestContext.Current.CancellationToken)
                    .WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Same(registerFailure, thrown);

            // The failure happened at the registration boundary, so NOTHING was built afterwards:
            // no stream was opened, no connection was published, and the runner never received a
            // provisioner callback — the exact opposite of the accepted path's evidence.
            Assert.Null(GetPublishedConnection(service));
            Assert.Equal(0, streamOpened);
            Assert.Null(runner.ConfigProvisioner);
            Assert.Equal(1, invoker.RegisterCalls);
        }
        finally
        {
            await JoinAllForTeardownAsync(service, priorFailure: null);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // (1a) The REAL RunAsync flow — negotiated completion-receipt ACK.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE OUTGOING NEGOTIATION REQUEST AND THE CAPTURED ANSWER, over the REAL
    /// <see cref="WorkerService.RunAsync"/>.
    /// <para>
    /// The production register request must ASK for completion-receipt ACKs explicitly. The
    /// orchestrator's ANSWER — parameterized over enabled and disabled — is then captured onto the
    /// connection that gets published, together with the existing assigned-ID fallback. The answer
    /// is the only source: it is never inferred from the orchestrator version, the advertised
    /// capabilities, or the assignment's model.
    /// </para>
    /// <para>
    /// PUBLICATION ORDER is observed at the initial Ready's own write, so "captured before
    /// publication" is a real ordering claim: the connection visible at that instant already carries
    /// the negotiated answer.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(true, "", LocalWorkerId)]
    [InlineData(true, AssignedWorkerId, AssignedWorkerId)]
    [InlineData(false, AssignedWorkerId, AssignedWorkerId)]
    public async Task RunAsync_RequestsCompletionReceiptAck_AndCapturesAcceptedAnswerBeforePublication(
        bool enabledByOrchestrator,
        string assignedWorkerIdFromOrchestrator,
        string expectedAssignedId)
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse
        {
            Accepted = true,
            AssignedWorkerId = assignedWorkerIdFromOrchestrator,
            OrchestratorVersion = "test",
            CompletionReceiptAckEnabled = enabledByOrchestrator,
        });

        var runner = new ProvisionerCapturingRunner();
        var service = BuildService(runner, new ProvisionerHarness().Provisioner);

        var requests = new RecordingRequestStream();
        var responses = new ChannelResponseReader();

        // Observed INSIDE the initial Ready write: the negotiated answer already on the published
        // connection AT that instant, so capture-before-publication is an ordering claim.
        bool? negotiatedAtFirstWrite = null;
        requests.OnWrite = _ =>
            negotiatedAtFirstWrite ??= GetPublishedConnection(service)?.CompletionReceiptAckEnabled;

        var stream = BuildRunStream(requests, responses, () => { });
        service.CallInvokerFactory = () => invoker;
        service.WorkStreamFactory = (_, _) => stream;

        using var loopCts = new CancellationTokenSource();
        Task<WorkerRunOutcome>? run = null;
        try
        {
            run = service.RunAsync(loopCts.Token);

            await requests.WaitForWriteCountAsync(1, TestContext.Current.CancellationToken);

            // THE OUTGOING REQUEST — a clone of what production actually sent.
            var sent = Assert.IsType<RegisterRequest>(invoker.LastRegisterRequest);
            Assert.True(
                sent.RequestCompletionReceiptAck,
                "The production registration must explicitly request completion-receipt ACKs.");
            Assert.Equal(LocalWorkerId, sent.WorkerId);

            // THE CAPTURED ANSWER, on the connection that was published.
            var connection = Assert.IsType<WorkerConnection>(GetPublishedConnection(service));
            Assert.Equal(enabledByOrchestrator, connection.CompletionReceiptAckEnabled);
            Assert.Equal(expectedAssignedId, connection.AssignedId);
            Assert.Equal(expectedAssignedId, requests.Writes[0].WorkerId);

            // ...and it was ALREADY on the connection when the initial Ready was written.
            Assert.Equal(enabledByOrchestrator, negotiatedAtFirstWrite);

            responses.TryComplete();
            var outcome = await run.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(WorkerRunOutcome.WorkStreamEnded, outcome);
        }
        finally
        {
            var cancellationFailure = await CancelForTeardownAsync(loopCts);
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, cancellationFailure, ("RunAsync", run));
        }
    }

    /// <summary>
    /// AN ABSENT ANSWER STAYS DISABLED. An orchestrator that never sets the field at all (an old
    /// one, parsing as the proto default) leaves the published connection disabled even though the
    /// worker asked — a REQUEST is never an ENABLEMENT.
    /// </summary>
    [Fact]
    public async Task RunAsync_AcceptedRegistrationWithoutAnswer_LeavesConnectionDisabled()
    {
        // Accepted, with an orchestrator version and capabilities-shaped inputs but NO answer field.
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse
        {
            Accepted = true,
            AssignedWorkerId = AssignedWorkerId,
            OrchestratorVersion = "99.99.99",
        });

        var runner = new ProvisionerCapturingRunner();
        var service = BuildService(runner, new ProvisionerHarness().Provisioner);

        var requests = new RecordingRequestStream();
        var responses = new ChannelResponseReader();
        var stream = BuildRunStream(requests, responses, () => { });
        service.CallInvokerFactory = () => invoker;
        service.WorkStreamFactory = (_, _) => stream;

        using var loopCts = new CancellationTokenSource();
        Task<WorkerRunOutcome>? run = null;
        try
        {
            run = service.RunAsync(loopCts.Token);
            await requests.WaitForWriteCountAsync(1, TestContext.Current.CancellationToken);

            Assert.True(invoker.LastRegisterRequest!.RequestCompletionReceiptAck);

            var connection = Assert.IsType<WorkerConnection>(GetPublishedConnection(service));
            Assert.False(
                connection.CompletionReceiptAckEnabled,
                "An absent answer must leave the connection disabled — a request is not an enablement.");

            responses.TryComplete();
            await run.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        finally
        {
            var cancellationFailure = await CancelForTeardownAsync(loopCts);
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, cancellationFailure, ("RunAsync", run));
        }
    }

    /// <summary>
    /// THE SECOND NEGOTIATED ANSWER — ordinary-readiness requirement — captured over the REAL
    /// <c>RunAsync</c> for EVERY combination of <c>CompletionReadyRequired</c> x
    /// <c>CompletionReceiptAckEnabled</c>, plus the default/omitted-field case.
    /// <para>
    /// WHAT IS PINNED, per combination, through the actual accepted-registration path from the fake
    /// <see cref="RegisterResponse"/>:
    /// <list type="bullet">
    ///   <item>each IMMUTABLE per-connection fact is exactly its own field of the accepted answer —
    ///   captured, never derived from the other flag, the version, the capabilities or a model;</item>
    ///   <item>the fact was ALREADY on the published connection at the instant the initial Ready was
    ///   written, which is the capture-before-publication ordering claim;</item>
    ///   <item>the initial post-registration Ready is written UNCONDITIONALLY in every mode — its
    ///   presence is the ungated assertion — and it carries the connection's own assigned ID;</item>
    ///   <item>the outgoing request is unchanged: one explicit ACK request, no readiness-request
    ///   field at all, so the requirement can only ever arrive as an ANSWER.</item>
    /// </list>
    /// No inference exists: two registrations that differ ONLY in one answer field produce
    /// connections that differ ONLY in that fact.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RunAsync_CapturesCompletionReadyRequiredPerCombination_ReadyStaysUngated(
        bool readyRequiredByOrchestrator, bool ackEnabledByOrchestrator)
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse
        {
            Accepted = true,
            AssignedWorkerId = AssignedWorkerId,
            OrchestratorVersion = "test",
            CompletionReceiptAckEnabled = ackEnabledByOrchestrator,
            CompletionReadyRequired = readyRequiredByOrchestrator,
        });

        var runner = new ProvisionerCapturingRunner();
        var service = BuildService(runner, new ProvisionerHarness().Provisioner);

        var requests = new RecordingRequestStream();
        var responses = new ChannelResponseReader();

        // Observed INSIDE the initial Ready write: BOTH facts on the published connection AT that
        // instant, so capture-before-publication is an ordering claim for each of them.
        bool? readyRequiredAtFirstWrite = null;
        bool? ackEnabledAtFirstWrite = null;
        requests.OnWrite = message =>
        {
            var connection = GetPublishedConnection(service);
            if (connection is null)
                return;
            readyRequiredAtFirstWrite ??= connection.CompletionReadyRequired;
            ackEnabledAtFirstWrite ??= connection.CompletionReceiptAckEnabled;
        };

        var stream = BuildRunStream(requests, responses, () => { });
        service.CallInvokerFactory = () => invoker;
        service.WorkStreamFactory = (_, _) => stream;

        using var loopCts = new CancellationTokenSource();
        Task<WorkerRunOutcome>? run = null;
        try
        {
            run = service.RunAsync(loopCts.Token);

            // THE INITIAL READY IS THE UNGATED WITNESS. In every mode the very first post-
            // registration write is the Ready: it never waits for a receipt, a completion or any
            // negotiated answer. Its arrival is awaited on its own bound.
            await requests.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);

            // THE OUTGOING REQUEST is unchanged by the new answer: the ACK request is explicit and
            // there is no readiness-request field at all — the requirement can only be an ANSWER.
            var sent = Assert.IsType<RegisterRequest>(invoker.LastRegisterRequest);
            Assert.True(
                sent.RequestCompletionReceiptAck,
                "The production registration must explicitly request completion-receipt ACKs.");
            Assert.Equal(LocalWorkerId, sent.WorkerId);

            // THE TWO CAPTURED FACTS, each exactly its own field of the accepted answer.
            var connection = Assert.IsType<WorkerConnection>(GetPublishedConnection(service));
            Assert.Equal(
                readyRequiredByOrchestrator, connection.CompletionReadyRequired);
            Assert.Equal(
                ackEnabledByOrchestrator, connection.CompletionReceiptAckEnabled);
            Assert.Equal(AssignedWorkerId, connection.AssignedId);

            // ...and BOTH were already on the connection when the initial Ready was written.
            Assert.Equal(readyRequiredByOrchestrator, readyRequiredAtFirstWrite);
            Assert.Equal(ackEnabledByOrchestrator, ackEnabledAtFirstWrite);

            // THE UNGATED READY ITSELF: exactly one Ready, carrying the connection's identity, and
            // written before anything else could have gated it (nothing else was ever sent).
            var ready = Assert.Single(requests.Writes);
            Assert.Equal(WorkerMessage.PayloadOneofCase.Ready, ready.PayloadCase);
            Assert.Equal(AssignedWorkerId, ready.WorkerId);

            responses.TryComplete();
            var outcome = await run.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(WorkerRunOutcome.WorkStreamEnded, outcome);
        }
        finally
        {
            var cancellationFailure = await CancelForTeardownAsync(loopCts);
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, cancellationFailure, ("RunAsync", run));
        }
    }

    /// <summary>
    /// AN ABSENT READINESS-REQUIREMENT ANSWER STAYS FALSE — the omitted-field case through the REAL
    /// registration flow. An orchestrator that never sets the field at all (an old one, parsing as
    /// the proto default) leaves the published connection's readiness requirement FALSE even though
    /// it answered ENABLED on the ACK field, which is the combination that proves the two facts are
    /// captured independently and neither implies the other.
    /// </summary>
    [Fact]
    public async Task RunAsync_AcceptedRegistrationWithoutReadyRequiredAnswer_LeavesRequirementDisabled()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse
        {
            Accepted = true,
            AssignedWorkerId = AssignedWorkerId,
            OrchestratorVersion = "99.99.99",
            CompletionReceiptAckEnabled = true,
        });

        var runner = new ProvisionerCapturingRunner();
        var service = BuildService(runner, new ProvisionerHarness().Provisioner);

        var requests = new RecordingRequestStream();
        var responses = new ChannelResponseReader();
        var stream = BuildRunStream(requests, responses, () => { });
        service.CallInvokerFactory = () => invoker;
        service.WorkStreamFactory = (_, _) => stream;

        using var loopCts = new CancellationTokenSource();
        Task<WorkerRunOutcome>? run = null;
        try
        {
            run = service.RunAsync(loopCts.Token);
            await requests.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);

            var connection = Assert.IsType<WorkerConnection>(GetPublishedConnection(service));
            Assert.False(
                connection.CompletionReadyRequired,
                "An absent readiness answer must leave the requirement disabled — it is never inferred.");
            Assert.True(
                connection.CompletionReceiptAckEnabled,
                "The ACK answer is its own fact and must not have been reset by the absent one.");

            responses.TryComplete();
            await run.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        finally
        {
            var cancellationFailure = await CancelForTeardownAsync(loopCts);
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, cancellationFailure, ("RunAsync", run));
        }
    }

    /// <summary>
    /// THE CONSTRUCTOR'S OPTIONAL READINESS-REQUIREMENT ARGUMENT defaults to false, exactly like the
    /// ACK argument. This calls the constructor shape directly without naming the new argument, so
    /// changing that default would fail while old callers and direct-loop fixtures remain
    /// source-compatible.
    /// </summary>
    [Fact]
    public void WorkerConnection_OmittedReadyRequiredArgument_DefaultsDisabled()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse { Accepted = true });
        var requests = new RecordingRequestStream();
        var responses = new ChannelResponseReader();
        using var stream = BuildRunStream(requests, responses, () => { });
        var connection = new WorkerConnection(
            AssignedWorkerId,
            new HiveOrchestrator.HiveOrchestratorClient(invoker),
            stream,
            provisionerOverride: null,
            includeProductionProvisioner: false);

        try
        {
            Assert.False(connection.CompletionReadyRequired);
            Assert.False(connection.CompletionReceiptAckEnabled);
        }
        finally
        {
            connection.Retire();
            responses.TryComplete();
        }
    }

    /// <summary>
    /// A REJECTED REGISTRATION OPENS AND PUBLISHES NOTHING even when the response carries
    /// <c>CompletionReadyRequired = true</c>: no stream, no connection, no captured readiness fact.
    /// The rejection is what stops everything else — the accepted-registration capture is the only
    /// source of the fact.
    /// </summary>
    [Fact]
    public async Task RunAsync_RejectedRegistrationWithReadyRequiredAnswer_PublishesNothing()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse
        {
            Accepted = false,
            CompletionReadyRequired = true,
            CompletionReceiptAckEnabled = true,
            AssignedWorkerId = AssignedWorkerId,
        });

        var runner = new ProvisionerCapturingRunner();
        var streamOpened = 0;
        var service = BuildService(runner, new ProvisionerHarness().Provisioner);

        try
        {
            service.CallInvokerFactory = () => invoker;
            service.WorkStreamFactory = (_, _) =>
            {
                Interlocked.Increment(ref streamOpened);
                throw new InvalidOperationException("No stream may be opened for a rejected registration.");
            };

            var outcome = await service.RunAsync(TestContext.Current.CancellationToken);
            Assert.Equal(WorkerRunOutcome.RegistrationRejected, outcome);

            Assert.Null(GetPublishedConnection(service));
            Assert.Equal(0, streamOpened);
            Assert.Null(runner.ConfigProvisioner);
        }
        finally
        {
            await JoinAllForTeardownAsync(service, priorFailure: null);
        }
    }

    /// <summary>
    /// SEQUENTIAL ISOLATION FOR THE READINESS REQUIREMENT. Run 1 is answered REQUIRE+ENABLED
    /// (the gated combination), run 2 (same service instance) is answered NEITHER: the second run's
    /// published connection must be a different object carrying its OWN two facts, retaining NOTHING
    /// from the first. The first connection — retired and unpublished — keeps its own captured
    /// facts, which proves the values are per-connection and not service-global state that merely
    /// happened to be overwritten. The initial Ready is ungated in BOTH runs.
    /// </summary>
    [Fact]
    public async Task SequentialRuns_ReadyRequiredThenNot_KeepEachConnectionOwnNegotiatedFacts()
    {
        var runner = new ProvisionerCapturingRunner();
        var service = BuildService(runner, new ProvisionerHarness().Provisioner);

        var firstResponses = new ChannelResponseReader();
        var secondResponses = new ChannelResponseReader();
        Task<WorkerRunOutcome>? firstRun = null;
        Task<WorkerRunOutcome>? secondRun = null;
        WorkerConnection? firstConnection = null;

        try
        {
            // RUN 1 — answered BOTH TRUE (the gated combination).
            {
                var invoker = new FakeOrchestratorInvoker(new RegisterResponse
                {
                    Accepted = true,
                    AssignedWorkerId = AssignedWorkerId,
                    CompletionReceiptAckEnabled = true,
                    CompletionReadyRequired = true,
                });
                var requests = new RecordingRequestStream();
                var stream = BuildRunStream(requests, firstResponses, () => { });
                service.CallInvokerFactory = () => invoker;
                service.WorkStreamFactory = (_, _) => stream;

                firstRun = service.RunAsync(TestContext.Current.CancellationToken);
                await requests.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);

                firstConnection = Assert.IsType<WorkerConnection>(GetPublishedConnection(service));
                Assert.True(firstConnection.CompletionReadyRequired);
                Assert.True(firstConnection.CompletionReceiptAckEnabled);

                firstResponses.TryComplete();
                await firstRun.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
                Assert.Null(GetPublishedConnection(service));
            }

            // RUN 2 — answered NEITHER, on the SAME service instance.
            {
                var invoker = new FakeOrchestratorInvoker(new RegisterResponse
                {
                    Accepted = true,
                    AssignedWorkerId = AssignedWorkerId,
                    CompletionReceiptAckEnabled = false,
                    CompletionReadyRequired = false,
                });
                var requests = new RecordingRequestStream();
                var stream = BuildRunStream(requests, secondResponses, () => { });
                service.CallInvokerFactory = () => invoker;
                service.WorkStreamFactory = (_, _) => stream;

                secondRun = service.RunAsync(TestContext.Current.CancellationToken);
                await requests.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);

                var secondConnection = Assert.IsType<WorkerConnection>(GetPublishedConnection(service));
                Assert.NotSame(firstConnection, secondConnection);
                Assert.False(
                    secondConnection.CompletionReadyRequired,
                    "A sequential run must retain NO readiness requirement from its predecessor.");
                Assert.False(
                    secondConnection.CompletionReceiptAckEnabled,
                    "A sequential run must retain NO ACK answer from its predecessor.");

                secondResponses.TryComplete();
                await secondRun.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            }

            // The retired predecessor keeps its OWN facts: the values are per-connection, not global.
            Assert.True(firstConnection!.IsRetired);
            Assert.True(firstConnection.CompletionReadyRequired);
            Assert.True(firstConnection.CompletionReceiptAckEnabled);
        }
        finally
        {
            firstResponses.TryComplete();
            secondResponses.TryComplete();
            await JoinAllForTeardownAsync(
                service, ("first RunAsync", firstRun), ("second RunAsync", secondRun));
        }
    }

    /// <summary>
    /// SEQUENTIAL ISOLATION, INVERSE DIRECTION: run 1 answers NEITHER, run 2 answers BOTH TRUE on
    /// the SAME service instance. The second connection gates on nothing inherited — its two facts
    /// arrive only from its OWN answer — yet its initial Ready is still ungated.
    /// </summary>
    [Fact]
    public async Task SequentialRuns_NotReadyRequiredThenReadyRequired_CapturesOnlyOwnAnswer()
    {
        var runner = new ProvisionerCapturingRunner();
        var service = BuildService(runner, new ProvisionerHarness().Provisioner);

        var firstResponses = new ChannelResponseReader();
        var secondResponses = new ChannelResponseReader();
        Task<WorkerRunOutcome>? firstRun = null;
        Task<WorkerRunOutcome>? secondRun = null;
        WorkerConnection? firstConnection = null;

        try
        {
            // RUN 1 — answered NEITHER.
            {
                var invoker = new FakeOrchestratorInvoker(new RegisterResponse
                {
                    Accepted = true,
                    AssignedWorkerId = AssignedWorkerId,
                });
                var requests = new RecordingRequestStream();
                var stream = BuildRunStream(requests, firstResponses, () => { });
                service.CallInvokerFactory = () => invoker;
                service.WorkStreamFactory = (_, _) => stream;

                firstRun = service.RunAsync(TestContext.Current.CancellationToken);
                await requests.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);

                firstConnection = Assert.IsType<WorkerConnection>(GetPublishedConnection(service));
                Assert.False(firstConnection.CompletionReadyRequired);
                Assert.False(firstConnection.CompletionReceiptAckEnabled);

                firstResponses.TryComplete();
                await firstRun.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
                Assert.Null(GetPublishedConnection(service));
            }

            // RUN 2 — answered BOTH TRUE, on the SAME service instance.
            {
                var invoker = new FakeOrchestratorInvoker(new RegisterResponse
                {
                    Accepted = true,
                    AssignedWorkerId = AssignedWorkerId,
                    CompletionReceiptAckEnabled = true,
                    CompletionReadyRequired = true,
                });
                var requests = new RecordingRequestStream();
                var stream = BuildRunStream(requests, secondResponses, () => { });
                service.CallInvokerFactory = () => invoker;
                service.WorkStreamFactory = (_, _) => stream;

                secondRun = service.RunAsync(TestContext.Current.CancellationToken);
                await requests.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);

                var secondConnection = Assert.IsType<WorkerConnection>(GetPublishedConnection(service));
                Assert.NotSame(firstConnection, secondConnection);
                Assert.True(
                    secondConnection.CompletionReadyRequired,
                    "The requirement comes ONLY from this run's own accepted answer.");
                Assert.True(
                    secondConnection.CompletionReceiptAckEnabled,
                    "The ACK answer comes ONLY from this run's own accepted answer.");

                secondResponses.TryComplete();
                await secondRun.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            }

            Assert.True(firstConnection!.IsRetired);
            Assert.False(firstConnection.CompletionReadyRequired);
        }
        finally
        {
            firstResponses.TryComplete();
            secondResponses.TryComplete();
            await JoinAllForTeardownAsync(
                service, ("first RunAsync", firstRun), ("second RunAsync", secondRun));
        }
    }

    /// <summary>
    /// REAL ACCEPTED-REGISTRATION → ASSIGNMENT → COMPLETE → ACK → ORDINARY READY, followed by a
    /// sequential accepted 10 registration on the SAME service that remains UNGATED. This is the
    /// production <see cref="WorkerService.RunAsync"/> path from the fake RegisterResponse — never
    /// a hand-built connection — and therefore proves the captured answers actually select the
    /// ordinary behavior used by the real message loop.
    /// <para>
    /// RUN 1 (11): initial Ready is ungated; after the assignment's Complete, ordinary Ready stays
    /// withheld while a ToolResponse is consumed; the exact ACK then emits exactly one ordinary
    /// Ready. RUN 2 (10): a new accepted connection receives ReadyRequired=true but AckEnabled=false
    /// and emits its ordinary Ready WITHOUT any ACK. The inverse answers cannot leak across runs.
    /// </para>
    /// <para>
    /// MUTATION PROOF. Changing production's <c>ackEnabled &amp;&amp; readyRequired</c> predicate to
    /// <c>readyRequired</c> gates run 2 and fails its no-ACK Ready rendezvous. Ignoring the accepted
    /// response or failing to publish its facts leaves run 1 ungated and fails its pre-ACK exact
    /// one-Ready assertion. A direct-loop-only implementation cannot satisfy this vector because
    /// both WorkerConnections are created and published only by <c>RunAsync</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SequentialRunAsync_Registered11GatesUntilAck_Registered10RemainsUngated()
    {
        var runner = new ProvisionerCapturingRunner();
        var service = BuildService(runner, new ProvisionerHarness().Provisioner);

        var firstResponses = new ChannelResponseReader();
        var secondResponses = new ChannelResponseReader();
        Task<WorkerRunOutcome>? firstRun = null;
        Task<WorkerRunOutcome>? secondRun = null;
        WorkerConnection? firstConnection = null;
        try
        {
            // RUN 1 — REAL accepted registration, BOTH answers true.
            {
                var invoker = new FakeOrchestratorInvoker(new RegisterResponse
                {
                    Accepted = true,
                    AssignedWorkerId = AssignedWorkerId,
                    CompletionReceiptAckEnabled = true,
                    CompletionReadyRequired = true,
                });
                var requests = new RecordingRequestStream();
                var stream = BuildRunStream(requests, firstResponses, () => { });
                service.CallInvokerFactory = () => invoker;
                service.WorkStreamFactory = (_, _) => stream;

                firstRun = service.RunAsync(TestContext.Current.CancellationToken);
                await requests.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);
                firstConnection = Assert.IsType<WorkerConnection>(GetPublishedConnection(service));
                Assert.True(firstConnection.CompletionReceiptAckEnabled);
                Assert.True(firstConnection.CompletionReadyRequired);

                firstResponses.Push(LifecycleAssignment("registered-gated")); // message 1
                await requests.WaitForWriteCountAsync(2, TestContext.Current.CancellationToken);
                var firstComplete = Assert.Single(
                    requests.Writes,
                    message => message.PayloadCase == WorkerMessage.PayloadOneofCase.Complete);
                Assert.Equal("registered-gated", firstComplete.Complete.TaskId);

                // The ordinary Ready is WITHHELD after Complete while the real loop still consumes
                // a ToolResponse. The probe is message 2 and its own barrier is not pre-satisfied.
                var withheldProbe = firstResponses.Consumed(2);
                Assert.False(withheldProbe.IsCompleted);
                firstResponses.Push(LifecycleProbe("registered-withheld"));
                await withheldProbe.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
                Assert.Single(
                    requests.Writes,
                    message => message.PayloadCase == WorkerMessage.PayloadOneofCase.Ready);

                // The exact ACK (message 3) through the real RunAsync loop authorizes exactly ONE
                // ordinary Ready. Message 4 is a handler-return barrier and is proved incomplete
                // before it is pushed.
                firstResponses.Push(new OrchestratorMessage
                {
                    CompletionReceiptAck = new CompletionReceiptAck
                    {
                        TaskId = "registered-gated",
                        WorkerId = AssignedWorkerId,
                    },
                });
                await requests.WaitForReadyCountAsync(2, TestContext.Current.CancellationToken);
                var ackHandlerReturned = firstResponses.Consumed(4);
                Assert.False(ackHandlerReturned.IsCompleted);
                firstResponses.Push(LifecycleProbe("registered-ack-returned"));
                await ackHandlerReturned.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

                Assert.Equal(
                    2,
                    requests.Writes.Count(
                        message => message.PayloadCase == WorkerMessage.PayloadOneofCase.Ready));
                Assert.Single(
                    requests.Writes,
                    message => message.PayloadCase == WorkerMessage.PayloadOneofCase.Complete);

                firstResponses.TryComplete();
                var outcome = await firstRun.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
                Assert.Equal(WorkerRunOutcome.WorkStreamEnded, outcome);
                Assert.Null(GetPublishedConnection(service));
            }

            // RUN 2 — same service, REAL accepted registration, READY-ONLY (10). The previous 11
            // gate cannot leak: ordinary Ready must arrive without any ACK.
            {
                var invoker = new FakeOrchestratorInvoker(new RegisterResponse
                {
                    Accepted = true,
                    AssignedWorkerId = AssignedWorkerId,
                    CompletionReceiptAckEnabled = false,
                    CompletionReadyRequired = true,
                });
                var requests = new RecordingRequestStream();
                var stream = BuildRunStream(requests, secondResponses, () => { });
                service.CallInvokerFactory = () => invoker;
                service.WorkStreamFactory = (_, _) => stream;

                secondRun = service.RunAsync(TestContext.Current.CancellationToken);
                await requests.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);
                var secondConnection = Assert.IsType<WorkerConnection>(GetPublishedConnection(service));
                Assert.NotSame(firstConnection, secondConnection);
                Assert.False(secondConnection.CompletionReceiptAckEnabled);
                Assert.True(secondConnection.CompletionReadyRequired);

                secondResponses.Push(LifecycleAssignment("registered-ready-only")); // message 1
                await requests.WaitForReadyCountAsync(2, TestContext.Current.CancellationToken);
                await requests.WaitForWriteCountAsync(3, TestContext.Current.CancellationToken);

                // NO ACK was ever pushed. Run 2 still produced exactly its initial + ordinary Ready,
                // with one Complete — the 10 combination is unequivocally ungated.
                Assert.Equal(
                    2,
                    requests.Writes.Count(
                        message => message.PayloadCase == WorkerMessage.PayloadOneofCase.Ready));
                var secondComplete = Assert.Single(
                    requests.Writes,
                    message => message.PayloadCase == WorkerMessage.PayloadOneofCase.Complete);
                Assert.Equal("registered-ready-only", secondComplete.Complete.TaskId);

                secondResponses.TryComplete();
                var outcome = await secondRun.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
                Assert.Equal(WorkerRunOutcome.WorkStreamEnded, outcome);
            }

            Assert.True(firstConnection!.IsRetired);
            Assert.True(firstConnection.CompletionReceiptAckEnabled);
            Assert.True(firstConnection.CompletionReadyRequired);
        }
        finally
        {
            firstResponses.TryComplete();
            secondResponses.TryComplete();
            await JoinAllForTeardownAsync(
                service, ("first registered RunAsync", firstRun), ("second registered RunAsync", secondRun));
        }
    }

    [Fact]
    public async Task RunAsync_RejectedRegistrationWithEnabledAnswer_PublishesNothing()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse
        {
            Accepted = false,
            CompletionReceiptAckEnabled = true,
            AssignedWorkerId = AssignedWorkerId,
        });

        var runner = new ProvisionerCapturingRunner();
        var streamOpened = 0;
        var service = BuildService(runner, new ProvisionerHarness().Provisioner);

        try
        {
            service.CallInvokerFactory = () => invoker;
            service.WorkStreamFactory = (_, _) =>
            {
                Interlocked.Increment(ref streamOpened);
                throw new InvalidOperationException("No stream may be opened for a rejected registration.");
            };

            var outcome = await service.RunAsync(TestContext.Current.CancellationToken);
            Assert.Equal(WorkerRunOutcome.RegistrationRejected, outcome);

            Assert.True(invoker.LastRegisterRequest!.RequestCompletionReceiptAck);
            Assert.Null(GetPublishedConnection(service));
            Assert.Equal(0, streamOpened);
            Assert.Null(runner.ConfigProvisioner);
        }
        finally
        {
            await JoinAllForTeardownAsync(service, priorFailure: null);
        }
    }

    /// <summary>
    /// SEQUENTIAL ISOLATION. Run 1 is answered ENABLED, run 2 (same service instance) is answered
    /// DISABLED: the second run's published connection must be a different object carrying its OWN
    /// disabled answer, retaining NOTHING from the first. The first connection — retired and
    /// unpublished — keeps its own captured fact, which proves the value is per-connection and not
    /// service-global state that merely happened to be overwritten.
    /// </summary>
    [Fact]
    public async Task SequentialRuns_EnabledThenDisabled_RetainNoNegotiationFromPreviousConnection()
    {
        var runner = new ProvisionerCapturingRunner();
        var service = BuildService(runner, new ProvisionerHarness().Provisioner);

        var firstResponses = new ChannelResponseReader();
        var secondResponses = new ChannelResponseReader();
        Task<WorkerRunOutcome>? firstRun = null;
        Task<WorkerRunOutcome>? secondRun = null;
        WorkerConnection? firstConnection = null;

        try
        {
            // RUN 1 — answered ENABLED.
            {
                var invoker = new FakeOrchestratorInvoker(new RegisterResponse
                {
                    Accepted = true,
                    AssignedWorkerId = AssignedWorkerId,
                    CompletionReceiptAckEnabled = true,
                });
                var requests = new RecordingRequestStream();
                var stream = BuildRunStream(requests, firstResponses, () => { });
                service.CallInvokerFactory = () => invoker;
                service.WorkStreamFactory = (_, _) => stream;

                firstRun = service.RunAsync(TestContext.Current.CancellationToken);
                await requests.WaitForWriteCountAsync(1, TestContext.Current.CancellationToken);

                firstConnection = Assert.IsType<WorkerConnection>(GetPublishedConnection(service));
                Assert.True(firstConnection.CompletionReceiptAckEnabled);

                firstResponses.TryComplete();
                await firstRun.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
                Assert.Null(GetPublishedConnection(service));
            }

            // RUN 2 — answered DISABLED, on the SAME service instance.
            {
                var invoker = new FakeOrchestratorInvoker(new RegisterResponse
                {
                    Accepted = true,
                    AssignedWorkerId = AssignedWorkerId,
                    CompletionReceiptAckEnabled = false,
                });
                var requests = new RecordingRequestStream();
                var stream = BuildRunStream(requests, secondResponses, () => { });
                service.CallInvokerFactory = () => invoker;
                service.WorkStreamFactory = (_, _) => stream;

                secondRun = service.RunAsync(TestContext.Current.CancellationToken);
                await requests.WaitForWriteCountAsync(1, TestContext.Current.CancellationToken);

                var secondConnection = Assert.IsType<WorkerConnection>(GetPublishedConnection(service));
                Assert.NotSame(firstConnection, secondConnection);
                Assert.False(
                    secondConnection.CompletionReceiptAckEnabled,
                    "A sequential run must retain NO negotiation fact from its predecessor.");

                // The request was still made on run 2 — only the ANSWER differed.
                Assert.True(invoker.LastRegisterRequest!.RequestCompletionReceiptAck);

                secondResponses.TryComplete();
                await secondRun.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            }

            // The retired predecessor keeps its OWN fact: the value is per-connection, not global.
            Assert.True(firstConnection!.IsRetired);
            Assert.True(firstConnection.CompletionReceiptAckEnabled);
        }
        finally
        {
            firstResponses.TryComplete();
            secondResponses.TryComplete();
            await JoinAllForTeardownAsync(
                service, ("first RunAsync", firstRun), ("second RunAsync", secondRun));
        }
    }

    /// <summary>
    /// THE PRODUCTION CONSTRUCTOR'S OPTIONAL ACK ARGUMENT defaults to disabled. This calls the old
    /// constructor shape directly (without naming or passing the new argument), so changing that
    /// default would fail while old callers and direct-loop fixtures remain source-compatible.
    /// </summary>
    [Fact]
    public void WorkerConnection_OmittedReceiptAckArgument_DefaultsDisabled()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse { Accepted = true });
        var requests = new RecordingRequestStream();
        var responses = new ChannelResponseReader();
        using var stream = BuildRunStream(requests, responses, () => { });
        var connection = new WorkerConnection(
            AssignedWorkerId,
            new HiveOrchestrator.HiveOrchestratorClient(invoker),
            stream,
            provisionerOverride: null,
            includeProductionProvisioner: false);

        try
        {
            Assert.False(connection.CompletionReceiptAckEnabled);
        }
        finally
        {
            connection.Retire();
            responses.TryComplete();
        }
    }

    /// <summary>
    /// THE CAPTURED RUNNER CALLBACK IS RETIREMENT-GATED EVEN WITH AN OVERRIDE PROVISIONER INSTALLED.
    /// <para>
    /// The override carries its OWN fetch delegate, which the connection knows nothing about — so if
    /// the raw <c>EnsureProvisionedAsync</c> were handed to the runner, the cached callback would
    /// keep provisioning after teardown. The witness is a FAILING provisioner fetch: it throws the
    /// instant it is entered, so reaching it at all is loudly distinguishable from the disconnected
    /// rejection, and its call count proves the underlying provisioner never started.
    /// </para>
    /// </summary>

    [Fact]
    public async Task CapturedRunnerCallback_WithOverrideProvisioner_FailsDisconnectedAfterRetirement()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse
        {
            Accepted = true,
            AssignedWorkerId = AssignedWorkerId,
        });

        // A witness provisioner whose fetch THROWS and counts: entering it is unmistakable.
        var entered = 0;
        var witness = new WorkerConfigProvisioner(
            "override-worker",
            (_, _) =>
            {
                Interlocked.Increment(ref entered);
                throw new InvalidOperationException("The underlying provisioner must not be started.");
            },
            _ => null,
            (_, _) => { });

        var runner = new ProvisionerCapturingRunner();
        var service = BuildService(runner, witness);

        var requests = new RecordingRequestStream();
        var responses = new ChannelResponseReader();
        var stream = new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
            requests, responses,
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ => { },
            null!);

        service.CallInvokerFactory = () => invoker;
        service.WorkStreamFactory = (_, _) => stream;

        using var loopCts = new CancellationTokenSource();
        Task<WorkerRunOutcome>? run = null;

        try
        {
            run = service.RunAsync(loopCts.Token);

            // BARRIER: the initial Ready is written strictly after publication, so observing it is
            // deterministic evidence that the connection is published — no polling, no sleeps.
            await requests.WaitForWriteCountAsync(1, TestContext.Current.CancellationToken);
            var connection = Assert.IsType<WorkerConnection>(GetPublishedConnection(service));

            // The OVERRIDE is what the connection carries, for BOTH sites.
            Assert.Same(witness, connection.Provisioner);
            Assert.NotNull(runner.ConfigProvisioner);

            // WHILE LIVE the callback really does reach the override — the witness throws its own
            // distinct failure, which is exactly how we know the callback is not inert.
            var live = await Assert.ThrowsAsync<InvalidOperationException>(
                () => runner.ConfigProvisioner!(FixtureModel, TestContext.Current.CancellationToken));
            Assert.Contains("must not be started", live.Message, StringComparison.Ordinal);
            Assert.Equal(1, Volatile.Read(ref entered));

            // AFTER RETIREMENT the SAME captured callback must fail DISCONNECTED and must NOT start
            // the override's transport: the entered count stays where it was.
            connection.Retire();
            var retired = await Assert.ThrowsAsync<InvalidOperationException>(
                () => runner.ConfigProvisioner!(FixtureModel, TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, retired.Message);
            Assert.Equal(1, Volatile.Read(ref entered));

            // The EAGER site's checked entry point behaves identically for the same override.
            var eager = await Assert.ThrowsAsync<InvalidOperationException>(
                () => connection.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, eager.Message);
            Assert.Equal(1, Volatile.Read(ref entered));
        }
        finally
        {
            var cancellationFailure = await CancelForTeardownAsync(loopCts);
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, cancellationFailure, ("RunAsync", run));
        }
    }

    /// <summary>
    /// A THROWING POST-PUBLICATION SETUP STEP still retires and unpublishes the connection BEFORE
    /// the stream/channel disposal callback runs.
    /// <para>
    /// <c>SetConfigProvisioner</c> is the fallible step used here (the interface permits an
    /// implementation to throw). Without cleanup covering the setup interval, lexical unwinding
    /// would dispose the transport while <c>_connection</c> stayed published and unretired — leaving
    /// a nominally usable connection backed by a disposed stream.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ThrowingPostPublicationSetup_RetiresAndUnpublishesBeforeStreamDisposal()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse
        {
            Accepted = true,
            AssignedWorkerId = AssignedWorkerId,
        });

        var setupFailure = new InvalidOperationException("SetConfigProvisioner failed during setup.");

        // Observed INSIDE the disposal callback: the state of the connection AT the moment the
        // transport is torn down. This is the ordering the fix establishes.
        WorkerConnection? published = null;
        var retiredAtDisposal = false;
        var unpublishedAtDisposal = false;
        var disposals = 0;

        WorkerService? serviceRef = null;

        // The FALLIBLE post-publication setup step. It runs after publication, so it is also the
        // deterministic point at which the published connection can be captured for the disposal
        // assertions below — and it was still PUBLISHED and USABLE right here.
        var publishedAtSetup = false;
        var runner = new ThrowingSetupRunner(() =>
        {
            published = GetPublishedConnection(serviceRef!);
            publishedAtSetup = published is { IsRetired: false };
            throw setupFailure;
        });

        var service = BuildService(runner, new ProvisionerHarness().Provisioner);
        serviceRef = service;
        var responses = new ChannelResponseReader();

        var stream = new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
            new RecordingRequestStream(),
            responses,
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ =>
            {
                Interlocked.Increment(ref disposals);
                retiredAtDisposal = published?.IsRetired ?? false;
                unpublishedAtDisposal = GetPublishedConnection(serviceRef!) is null;
            },
            null!);

        service.CallInvokerFactory = () => invoker;
        service.WorkStreamFactory = (_, _) => stream;

        Task<WorkerRunOutcome>? run = null;
        try
        {
            run = service.RunAsync(TestContext.Current.CancellationToken);

            // The setup step throws, so the failure propagates to the caller unchanged.
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => run.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Same(setupFailure, thrown);

            // The connection really was published and usable when the fallible step ran — otherwise the
            // teardown assertions below would be vacuous.
            Assert.NotNull(published);
            Assert.True(publishedAtSetup, "The connection must be published and usable at the setup step.");

            // ...and the cleanup that now covers the setup interval retired and unpublished it BEFORE
            // the transport was disposed.
            Assert.Null(GetPublishedConnection(service));
            Assert.True(published!.IsRetired);
            Assert.Equal(1, disposals);
            Assert.True(retiredAtDisposal, "The connection must be retired before its stream is disposed.");
            Assert.True(unpublishedAtDisposal, "The connection must be unpublished before its stream is disposed.");

            // THE DETACH RAN ON THE EARLY SETUP-FAILURE PATH TOO. The run installed a callback only
            // for the install attempt to throw, yet the lifecycle still detached (a SECOND
            // SetConfigProvisioner call receiving null) before releasing the run guard — exactly as
            // the setup-success paths do. The install call is first, so a missing detach leaves this
            // count at 1 and fails by name.
            Assert.Equal(2, runner.SetConfigProvisionerCalls);
            Assert.True(runner.Detached, "The run's provisioning callback must be detached even on an early setup failure.");

            // And the service is genuinely disconnected afterwards — no nominally usable connection.
            var sessionFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.GetSessionAsync("goal:role", TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, sessionFailure.Message);
        }
        finally
        {
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, ("RunAsync", run));
        }
    }

    /// <summary>
    /// THE EAGER PER-ASSIGNMENT SITE IS RETIREMENT-GATED TOO, for the same override provisioner.
    /// <para>
    /// This drives the REAL message loop through two assignments on one connection. The first runs
    /// while the connection is live, so the eager site provably REACHES the override (the witness
    /// fetch is entered) — that is the non-vacuity check. The connection is then retired and a
    /// second assignment is delivered: the eager site must fail with the disconnected error and must
    /// NOT start the override, so the witness count does not move.
    /// </para>
    /// </summary>
    [Fact]
    public async Task EagerProvisioningSite_WithOverrideProvisioner_IsRetirementGated()
    {
        // A witness provisioner whose fetch THROWS and counts: entering it is unmistakable, and it
        // fails before any config-repo seam work, so the assignment body needs no git at all.
        var entered = 0;
        var witness = new WorkerConfigProvisioner(
            "override-worker",
            (_, _) =>
            {
                Interlocked.Increment(ref entered);
                throw new InvalidOperationException("The underlying provisioner must not be started.");
            },
            _ => null,
            (_, _) => { });

        var service = BuildService(new ProvisionerCapturingRunner(), witness);

        var requests = new RecordingRequestStream();
        var responses = new ChannelResponseReader();
        var stream = new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
            requests, responses,
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ => { },
            null!);

        // A connection carrying the OVERRIDE for both sites, driven through the real loop.
        var connection = TestConnectionFactory.Attach(service, AssignedWorkerId, stream, witness);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);

        try
        {
            // LIVE: the eager site reaches the override. The body catches the witness failure and
            // still emits its single Ready, which is the deterministic barrier.
            responses.Push(Assignment("task-live"));
            await requests.WaitForReadyCountAsync(1, TestContext.Current.CancellationToken);
            Assert.Equal(1, Volatile.Read(ref entered));

            // RETIRED: the second assignment's eager site must fail disconnected without starting
            // the override. Its Ready cannot be written on a retired connection, so EOF plus the
            // loop's own teardown drain is the barrier that proves the body finished.
            connection.Retire();
            responses.Push(Assignment("task-retired"));
            responses.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Equal(1, Volatile.Read(ref entered));
        }
        finally
        {
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, ("message loop", loop));
        }
    }

    /// <summary>
    /// A THROWING HEARTBEAT CANCELLATION CALLBACK CANNOT SKIP JOINING THE ORIGINAL HEARTBEAT TASK.
    /// <para>
    /// The controlled heartbeat task provided by <c>HeartbeatTaskFactory</c> is deliberately NOT a
    /// real heartbeat RPC: it parks until the test releases it and never observes the token, so the
    /// ONLY thing that can complete it is the test. While it is unreleased, teardown must stay
    /// parked in <c>await heartbeatTask</c>: the source is not yet disposed, the transport is not
    /// yet disposed, and <see cref="WorkerService.RunAsync"/> has not returned.
    /// </para>
    /// <para>
    /// After release, the deferred cancellation failure surfaces on the run task — with the
    /// runtime's own <see cref="AggregateException"/> wrapper and the callback's original exception
    /// inside it — and only then are the source and the transport disposed. The seam was entered
    /// exactly once with the ACTUAL owned source, so the task joined is provably the SAME control
    /// task the factory returned: no live heartbeat is abandoned.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunAsync_ThrowingHeartbeatCancellationCallback_JoinsOriginalTaskBeforeDisposingSourceAndTransport()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse
        {
            Accepted = true,
            AssignedWorkerId = AssignedWorkerId,
        });
        var runner = new ProvisionerCapturingRunner();
        var service = BuildService(runner, new ProvisionerHarness().Provisioner);

        var requests = new RecordingRequestStream();
        var responses = new ChannelResponseReader();

        // Hoisted so the stream's disposal callback observes the ACTUAL connection's teardown state.
        WorkerConnection? connection = null;
        var retiredAtStreamDisposal = false;
        var unpublishedAtStreamDisposal = false;
        var streamDisposals = 0;
        var stream = new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
            requests, responses,
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ =>
            {
                Interlocked.Increment(ref streamDisposals);
                retiredAtStreamDisposal = connection?.IsRetired ?? false;
                unpublishedAtStreamDisposal = GetPublishedConnection(service) is null;
            },
            null!);

        var heartbeatEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeatGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeatFactoryCalls = 0;
        var heartbeatJoinFailure = new HeartbeatJoinFailureException("controlled heartbeat join failure");
        CancellationTokenSource? ownedHeartbeatCts = null;
        Task? controlledHeartbeatTask = null;
        service.HeartbeatTaskFactory = (_, cts) =>
        {
            Interlocked.Increment(ref heartbeatFactoryCalls);
            ownedHeartbeatCts = cts;
            heartbeatEntered.TrySetResult();
            controlledHeartbeatTask = ControlledHeartbeatAsync();
            return controlledHeartbeatTask;

            // The controlled task replaces the production heartbeat loop at its existing launch
            // point. It parks until released and then faults with unique evidence. The resulting
            // guarded join diagnostic is the positive signal that RunAsync awaited THIS task.
            async Task ControlledHeartbeatAsync()
            {
                await heartbeatGate.Task;
                throw heartbeatJoinFailure;
            }
        };

        service.CallInvokerFactory = () => invoker;
        service.WorkStreamFactory = (_, _) => stream;

        var originalErr = Console.Error;
        var stdErr = new StringWriter();
        var joinObserved = new MarkerObservingWriter("Heartbeat join failed", stdErr);
        using var loopCts = new CancellationTokenSource();
        Task<WorkerRunOutcome>? run = null;
        CancellationTokenRegistration registration = default;
        try
        {
            run = service.RunAsync(loopCts.Token);

            // The seam ran at the EXISTING launch point (after publication), with the ACTUAL owned
            // heartbeat source, and it returned the ONE task teardown must join.
            await requests.WaitForWriteCountAsync(1, TestContext.Current.CancellationToken);
            await heartbeatEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, Volatile.Read(ref heartbeatFactoryCalls));
            var heartbeatCts = Assert.IsAssignableFrom<CancellationTokenSource>(ownedHeartbeatCts);
            var joinedTask = Assert.IsAssignableFrom<Task>(controlledHeartbeatTask);
            Assert.False(joinedTask.IsCompleted);
            connection = Assert.IsType<WorkerConnection>(GetPublishedConnection(service));

            // The callback records its invocation AND throws a type distinct from every competing
            // failure, so propagated/reported evidence cannot be misclassified.
            var callbackFailure = new HeartbeatCancellationCallbackException(
                "throwing heartbeat cancellation callback");
            var callbackInvoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            registration = heartbeatCts.Token.Register(() =>
            {
                callbackInvoked.TrySetResult();
                throw callbackFailure;
            });

            Console.SetError(joinObserved);

            // EOF ends the body, so RunAsync's cleanup requests cancellation — and the callback throws.
            responses.TryComplete();
            await callbackInvoked.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // CLEANUP MUST STILL BE PARKED IN THE JOIN. The controlled task is held, so the source is
            // undisposed, the transport is undisposed and the run has not returned.
            Assert.False(joinedTask.IsCompleted, "The ORIGINAL heartbeat task must still be the one being awaited.");
            Assert.False(run.IsCompleted, "Teardown must not return while the original heartbeat task is still running.");
            Assert.Null(Record.Exception(() => _ = heartbeatCts.Token));
            Assert.True(heartbeatCts.IsCancellationRequested, "The cancellation request must still have taken effect.");
            Assert.Equal(0, Volatile.Read(ref streamDisposals));

            // Release the ORIGINAL task: it faults with unique evidence. The join diagnostic is a
            // positive production signal that RunAsync awaited this exact task; a skipped join can
            // neither fabricate this classification nor complete the marker.
            heartbeatGate.TrySetResult();
            await joinObserved.MarkerObserved.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var surfaced = await Assert.ThrowsAnyAsync<Exception>(
                () => run.WaitAsync(Failsafe, TestContext.Current.CancellationToken));

            // The deferred failure kept its ORIGINAL evidence, wrapped exactly as the runtime produced it.
            Assert.IsType<AggregateException>(surfaced);
            Assert.Contains(callbackFailure, Flatten(surfaced));

            // The SAME task was joined, the source is now disposed, and the transport went away only
            // after the join — with retirement/unpublication still preceding disposal.
            Assert.True(joinedTask.IsCompleted, "The task the factory returned must have been joined.");
            Assert.Throws<ObjectDisposedException>(() => _ = heartbeatCts.Token);
            Assert.Equal(1, Volatile.Read(ref streamDisposals));
            Assert.True(retiredAtStreamDisposal, "The connection must be retired before its stream is disposed.");
            Assert.True(unpublishedAtStreamDisposal, "The connection must be unpublished before its stream is disposed.");

            var diagnostics = stdErr.ToString();
            Assert.Contains(nameof(HeartbeatJoinFailureException), diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(heartbeatJoinFailure.Message, diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalErr);
            registration.Dispose();
            heartbeatGate.TrySetResult();
            var cancellationFailure = await CancelForTeardownAsync(loopCts);
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, cancellationFailure,
                ("controlled heartbeat task", controlledHeartbeatTask), ("RunAsync", run));
        }
    }

    /// <summary>
    /// A PRIMARY <c>RunAsync</c> FAILURE IS PRESERVED when the cancellation cleanup ALSO fails: the
    /// primary propagates with its original identity, the join of the ORIGINAL heartbeat task still
    /// happens, and the secondary failure is reported through the EXISTING sanitized logger seam
    /// (type classification only, never the message) without replacing the real failure.
    /// </summary>
    [Fact]
    public async Task RunAsync_PrimaryFailureWithThrowingHeartbeatCallback_PreservesPrimaryAndStillJoins()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse
        {
            Accepted = true,
            AssignedWorkerId = AssignedWorkerId,
        });
        var runner = new ProvisionerCapturingRunner();
        var service = BuildService(runner, new ProvisionerHarness().Provisioner);

        // The PRIMARY failure: the initial Ready write fails, inside RunAsync's covered body.
        var primaryFailure = new PrimaryTransportFailureException("primary transport failure");

        var requests = new RecordingRequestStream { FailNextReadyWrite = primaryFailure };
        var responses = new ChannelResponseReader();
        var streamDisposals = 0;
        var stream = new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
            requests, responses,
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ => Interlocked.Increment(ref streamDisposals),
            null!);

        var originalErr = Console.Error;
        var stdErr = new StringWriter();
        var joinObserved = new MarkerObservingWriter("Heartbeat join failed", stdErr);

        var heartbeatEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeatGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeatFactoryCalls = 0;
        var heartbeatJoinFailure = new HeartbeatJoinFailureException("controlled heartbeat join failure");
        CancellationTokenSource? ownedHeartbeatCts = null;
        Task? controlledHeartbeatTask = null;

        // The callback records its invocation AND throws. Its distinct type proves the secondary,
        // rather than the primary, is what the sanitized cancellation diagnostic classifies.
        var callbackFailure = new HeartbeatCancellationCallbackException(
            "throwing heartbeat cancellation callback");
        var callbackInvoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;

        service.HeartbeatTaskFactory = (_, cts) =>
        {
            Interlocked.Increment(ref heartbeatFactoryCalls);
            ownedHeartbeatCts = cts;
            registration = cts.Token.Register(() =>
            {
                callbackInvoked.TrySetResult();
                throw callbackFailure;
            });
            heartbeatEntered.TrySetResult();
            controlledHeartbeatTask = ControlledHeartbeatAsync();
            return controlledHeartbeatTask;

            // The controlled task parks until released and then faults with a distinct sentinel.
            // Its guarded join diagnostic is the positive signal that THIS task was awaited.
            async Task ControlledHeartbeatAsync()
            {
                await heartbeatGate.Task;
                throw heartbeatJoinFailure;
            }
        };

        service.CallInvokerFactory = () => invoker;
        service.WorkStreamFactory = (_, _) => stream;

        using var loopCts = new CancellationTokenSource();
        Task<WorkerRunOutcome>? run = null;
        try
        {
            run = service.RunAsync(loopCts.Token);

            // The heartbeat had already been launched at its unchanged launch point BEFORE the
            // primary failure, so the cleanup below always has a running heartbeat to join.
            await heartbeatEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var heartbeatCts = Assert.IsAssignableFrom<CancellationTokenSource>(ownedHeartbeatCts);
            var joinedTask = Assert.IsAssignableFrom<Task>(controlledHeartbeatTask);

            // The diagnostics below go to the existing logger seam. The marker gives a positive,
            // bounded signal only after the original heartbeat task's join fault was observed.
            Console.SetError(joinObserved);

            // The secondary failure fired during cleanup; the join must still be parked on the
            // ORIGINAL controlled task.
            await callbackInvoked.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.False(joinedTask.IsCompleted);
            Assert.False(run.IsCompleted, "The join must still occur even though a primary failure exists.");
            Assert.Equal(0, Volatile.Read(ref streamDisposals));

            heartbeatGate.TrySetResult();
            await joinObserved.MarkerObserved.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // The PRIMARY propagates — unchanged, with its own identity.
            var thrown = await Assert.ThrowsAsync<PrimaryTransportFailureException>(
                () => run.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Same(primaryFailure, thrown);

            // The join happened, the source was disposed, and the transport went away afterwards.
            Assert.True(joinedTask.IsFaulted);
            Assert.Throws<ObjectDisposedException>(() => _ = heartbeatCts.Token);
            Assert.Equal(1, Volatile.Read(ref streamDisposals));

            // The secondary failure was REPORTED in sanitized form — classified by type, never by
            // message — so the provisioned-content redaction contract is unchanged.
            var diagnostics = stdErr.ToString();
            Assert.Contains("Heartbeat cancellation cleanup failed", diagnostics, StringComparison.Ordinal);
            Assert.Contains(
                nameof(HeartbeatCancellationCallbackException), diagnostics, StringComparison.Ordinal);
            Assert.Contains("Heartbeat join failed", diagnostics, StringComparison.Ordinal);
            Assert.Contains(nameof(HeartbeatJoinFailureException), diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(nameof(PrimaryTransportFailureException), diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(callbackFailure.Message, diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalErr);
            registration.Dispose();
            heartbeatGate.TrySetResult();
            var cancellationFailure = await CancelForTeardownAsync(loopCts);
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, cancellationFailure,
                ("controlled heartbeat task", controlledHeartbeatTask), ("RunAsync", run));
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // (1b) The heartbeat JOIN outcome (defect A).
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A FAULTING ORIGINAL HEARTBEAT TASK CANNOT BYPASS <c>heartbeatCts.Dispose()</c> NOR REPLACE A
    /// PRIOR PRIMARY.
    /// <para>
    /// The controlled heartbeat task faults with a NON-cancellation exception (the production
    /// analogue: a heartbeat tick whose diagnostic sink is degraded), while a PRIMARY transport
    /// failure is already in flight from <c>RunAsync</c>'s covered body. The join outcome must be
    /// captured: the source is still disposed, the transport is still disposed, and the PRIMARY is
    /// what surfaces — the heartbeat fault is only REPORTED through the existing guarded sanitized
    /// logging (type classification, never the message).
    /// </para>
    /// <para>
    /// REMOVAL PROOF. If the join outcome escapes the cleanup <c>finally</c>, the source is never
    /// disposed (the <c>ObjectDisposedException</c> assertion fails) and the surfaced exception is
    /// the heartbeat fault rather than the primary (the <c>Assert.Same</c> fails), so both named
    /// assertions fail together.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunAsync_FaultingHeartbeatTaskWithPrimary_DisposesSourceAndPrimarySurfaces()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse
        {
            Accepted = true,
            AssignedWorkerId = AssignedWorkerId,
        });
        var service = BuildService(new ProvisionerCapturingRunner(), new ProvisionerHarness().Provisioner);

        // The PRIMARY failure: the initial Ready write fails, inside RunAsync's covered body.
        var primaryFailure = new PrimaryTransportFailureException("primary transport failure");

        var requests = new RecordingRequestStream { FailNextReadyWrite = primaryFailure };
        var responses = new ChannelResponseReader();
        var streamDisposals = 0;
        var stream = new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
            requests, responses,
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ => Interlocked.Increment(ref streamDisposals),
            null!);

        // The ORIGINAL heartbeat task's NON-cancellation fault has a distinct type, so the
        // diagnostic cannot accidentally classify the primary and still satisfy the assertion.
        var heartbeatFault = new HeartbeatJoinFailureException("heartbeat task fault");
        var heartbeatEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeatGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource? ownedHeartbeatCts = null;
        Task? controlledHeartbeatTask = null;
        service.HeartbeatTaskFactory = (_, cts) =>
        {
            ownedHeartbeatCts = cts;
            heartbeatEntered.TrySetResult();
            controlledHeartbeatTask = ControlledHeartbeatAsync();
            return controlledHeartbeatTask;

            // Replaces the production heartbeat LOOP at the existing launch point; no RPC, no tick.
            // It parks until the test releases it and THEN faults, so the fault can only be
            // observed by a teardown that actually joined this ORIGINAL task.
            async Task ControlledHeartbeatAsync()
            {
                await heartbeatGate.Task;
                throw heartbeatFault;
            }
        };

        service.CallInvokerFactory = () => invoker;
        service.WorkStreamFactory = (_, _) => stream;

        var originalErr = Console.Error;
        var stdErr = new StringWriter();
        using var loopCts = new CancellationTokenSource();
        Task<WorkerRunOutcome>? run = null;
        try
        {
            run = service.RunAsync(loopCts.Token);

            await heartbeatEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var heartbeatCts = Assert.IsAssignableFrom<CancellationTokenSource>(ownedHeartbeatCts);
            var joinedTask = Assert.IsAssignableFrom<Task>(controlledHeartbeatTask);

            // The diagnostics below go to the EXISTING logger seam (Console.Error).
            Console.SetError(stdErr);

            // The primary already failed, yet teardown is parked joining the ORIGINAL task: nothing
            // is disposed while it still runs.
            Assert.False(joinedTask.IsCompleted);
            Assert.False(run.IsCompleted, "Teardown must not return while the original heartbeat task is still running.");
            Assert.Null(Record.Exception(() => _ = heartbeatCts.Token));
            Assert.Equal(0, Volatile.Read(ref streamDisposals));

            // Release the ORIGINAL task so it faults; the join observes that fault.
            heartbeatGate.TrySetResult();

            // The PRIMARY propagates — unchanged, with its own identity.
            var thrown = await Assert.ThrowsAsync<PrimaryTransportFailureException>(
                () => run.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Same(primaryFailure, thrown);

            // THE JOIN OUTCOME DID NOT BYPASS DISPOSAL: the source is released and the transport
            // went away after the join.
            Assert.True(joinedTask.IsFaulted, "The ORIGINAL heartbeat task must have faulted and been joined.");
            Assert.Throws<ObjectDisposedException>(() => _ = heartbeatCts.Token);
            Assert.Equal(1, Volatile.Read(ref streamDisposals));

            // The heartbeat fault was REPORTED in sanitized form — type only, never the message.
            var diagnostics = stdErr.ToString();
            Assert.Contains("Heartbeat join failed", diagnostics, StringComparison.Ordinal);
            Assert.Contains(nameof(HeartbeatJoinFailureException), diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(nameof(PrimaryTransportFailureException), diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(heartbeatFault.Message, diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalErr);
            heartbeatGate.TrySetResult();
            var cancellationFailure = await CancelForTeardownAsync(loopCts);
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, cancellationFailure,
                ("controlled heartbeat task", controlledHeartbeatTask), ("RunAsync", run));
        }
    }

    /// <summary>
    /// WITH NO PRIOR PRIMARY, a faulting ORIGINAL heartbeat task still cannot bypass
    /// <c>heartbeatCts.Dispose()</c>: the fault propagates from <c>RunAsync</c> only AFTER the join
    /// and the disposal have completed, with its ORIGINAL identity preserved.
    /// <para>
    /// THE HELD-WORK CASE, reused here rather than duplicated across the ownership matrix: while the
    /// ORIGINAL controlled heartbeat task is still held, NO <see cref="WorkerRunOutcome"/> is
    /// observable at all (the run handle is incomplete, so reading a result is impossible); after
    /// release, the ORIGINAL cleanup outcome — the heartbeat fault, unchanged — is what the caller
    /// observes, never a fabricated successful outcome.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunAsync_FaultingHeartbeatTaskWithoutPrimary_DisposesSourceThenPropagatesFault()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse
        {
            Accepted = true,
            AssignedWorkerId = AssignedWorkerId,
        });
        var service = BuildService(new ProvisionerCapturingRunner(), new ProvisionerHarness().Provisioner);

        var requests = new RecordingRequestStream();
        var responses = new ChannelResponseReader();
        var streamDisposals = 0;
        var stream = new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
            requests, responses,
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ => Interlocked.Increment(ref streamDisposals),
            null!);

        var heartbeatFault = new HeartbeatJoinFailureException("heartbeat task fault");
        var heartbeatEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeatGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource? ownedHeartbeatCts = null;
        Task? controlledHeartbeatTask = null;
        CancellationTokenRegistration registration = default;
        service.HeartbeatTaskFactory = (_, cts) =>
        {
            ownedHeartbeatCts = cts;
            registration = cts.Token.Register(() => cancellationObserved.TrySetResult());
            heartbeatEntered.TrySetResult();
            controlledHeartbeatTask = ControlledHeartbeatAsync();
            return controlledHeartbeatTask;

            async Task ControlledHeartbeatAsync()
            {
                await heartbeatGate.Task;
                throw heartbeatFault;
            }
        };

        service.CallInvokerFactory = () => invoker;
        service.WorkStreamFactory = (_, _) => stream;

        using var loopCts = new CancellationTokenSource();
        Task<WorkerRunOutcome>? run = null;
        try
        {
            run = service.RunAsync(loopCts.Token);

            // The connection is live and the heartbeat launched: the initial Ready proves it.
            await requests.WaitForWriteCountAsync(1, TestContext.Current.CancellationToken);
            await heartbeatEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var heartbeatCts = Assert.IsAssignableFrom<CancellationTokenSource>(ownedHeartbeatCts);
            var joinedTask = Assert.IsAssignableFrom<Task>(controlledHeartbeatTask);

            // Ordinary EOF ends the body with NO primary failure at all. The callback signal proves
            // teardown actually requested cancellation before the pre-release state is inspected.
            responses.TryComplete();
            await cancellationObserved.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // Teardown is parked joining the ORIGINAL task; nothing has been disposed.
            Assert.False(joinedTask.IsCompleted);
            Assert.False(run.IsCompleted, "Teardown must not return while the original heartbeat task is still running.");
            Assert.Null(Record.Exception(() => _ = heartbeatCts.Token));
            Assert.Equal(0, Volatile.Read(ref streamDisposals));

            // NO RUN RESULT EXISTS WHILE THE ORIGINAL WORK IS STILL HELD: the ONLY thing that can
            // complete the controlled heartbeat task is this test, so the run handle is provably
            // incomplete here — an incomplete task yields no WorkerRunOutcome at all.
            Assert.False(
                run.IsCompleted,
                "No run outcome may be observable while the original heartbeat work is still held.");

            heartbeatGate.TrySetResult();

            // The ORIGINAL CLEANUP OUTCOME, after release, is the heartbeat fault with its ORIGINAL
            // identity — the run still faults rather than fabricating a successful outcome.
            var thrown = await Assert.ThrowsAsync<HeartbeatJoinFailureException>(
                () => run.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Same(heartbeatFault, thrown);
            Assert.True(run.IsFaulted, "The run must fault, not report a returned outcome.");

            // ...raised only AFTER the join and the disposal completed.
            Assert.True(joinedTask.IsFaulted);
            Assert.Throws<ObjectDisposedException>(() => _ = heartbeatCts.Token);
            Assert.Equal(1, Volatile.Read(ref streamDisposals));
        }
        finally
        {
            registration.Dispose();
            heartbeatGate.TrySetResult();
            var cancellationFailure = await CancelForTeardownAsync(loopCts);
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, cancellationFailure,
                ("controlled heartbeat task", controlledHeartbeatTask), ("RunAsync", run));
        }
    }

    /// <summary>
    /// A CANCELLATION-CALLBACK FAILURE IS NEVER REPLACED OR DISCARDED BY A HEARTBEAT-JOIN FAILURE.
    /// <para>
    /// With NO prior primary, both cleanup failures occur: the cancellation callback throws AND the
    /// ORIGINAL heartbeat task then faults. The captured cancellation evidence is the authoritative
    /// outcome (kept verbatim, inside the runtime's own <see cref="AggregateException"/> wrapper),
    /// the heartbeat fault is only REPORTED, and the source is still disposed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunAsync_CallbackFailureAndFaultingHeartbeat_CallbackEvidenceWinsAndSourceDisposed()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse
        {
            Accepted = true,
            AssignedWorkerId = AssignedWorkerId,
        });
        var service = BuildService(new ProvisionerCapturingRunner(), new ProvisionerHarness().Provisioner);

        var requests = new RecordingRequestStream();
        var responses = new ChannelResponseReader();
        var streamDisposals = 0;
        var stream = new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
            requests, responses,
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ => Interlocked.Increment(ref streamDisposals),
            null!);

        var heartbeatFault = new HeartbeatJoinFailureException("heartbeat task fault");
        var callbackFailure = new HeartbeatCancellationCallbackException(
            "throwing heartbeat cancellation callback");
        var callbackInvoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeatEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeatGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource? ownedHeartbeatCts = null;
        Task? controlledHeartbeatTask = null;
        CancellationTokenRegistration registration = default;
        service.HeartbeatTaskFactory = (_, cts) =>
        {
            ownedHeartbeatCts = cts;
            registration = cts.Token.Register(() =>
            {
                callbackInvoked.TrySetResult();
                throw callbackFailure;
            });
            heartbeatEntered.TrySetResult();
            controlledHeartbeatTask = ControlledHeartbeatAsync();
            return controlledHeartbeatTask;

            async Task ControlledHeartbeatAsync()
            {
                await heartbeatGate.Task;
                throw heartbeatFault;
            }
        };

        service.CallInvokerFactory = () => invoker;
        service.WorkStreamFactory = (_, _) => stream;

        var originalErr = Console.Error;
        var stdErr = new StringWriter();
        var joinObserved = new MarkerObservingWriter("Heartbeat join failed", stdErr);
        using var loopCts = new CancellationTokenSource();
        Task<WorkerRunOutcome>? run = null;
        try
        {
            run = service.RunAsync(loopCts.Token);

            await requests.WaitForWriteCountAsync(1, TestContext.Current.CancellationToken);
            await heartbeatEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var heartbeatCts = Assert.IsAssignableFrom<CancellationTokenSource>(ownedHeartbeatCts);
            var joinedTask = Assert.IsAssignableFrom<Task>(controlledHeartbeatTask);

            Console.SetError(joinObserved);

            // EOF ends the body with NO primary; the cancellation request then raises the callback
            // failure, and the join must still be parked on the ORIGINAL task.
            responses.TryComplete();
            await callbackInvoked.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.False(joinedTask.IsCompleted);
            Assert.False(run.IsCompleted, "Teardown must not return while the original heartbeat task is still running.");
            Assert.Null(Record.Exception(() => _ = heartbeatCts.Token));
            Assert.Equal(0, Volatile.Read(ref streamDisposals));

            heartbeatGate.TrySetResult();
            await joinObserved.MarkerObserved.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // THE CALLBACK EVIDENCE WINS — verbatim, inside the runtime's own wrapper — and is
            // neither replaced nor discarded by the heartbeat fault.
            var surfaced = await Assert.ThrowsAnyAsync<Exception>(
                () => run.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.IsType<AggregateException>(surfaced);
            Assert.Contains(callbackFailure, Flatten(surfaced));
            Assert.DoesNotContain(heartbeatFault, Flatten(surfaced));

            // The join still happened and the source was still disposed.
            Assert.True(joinedTask.IsFaulted);
            Assert.Throws<ObjectDisposedException>(() => _ = heartbeatCts.Token);
            Assert.Equal(1, Volatile.Read(ref streamDisposals));

            // The non-authoritative heartbeat fault was reported, sanitized.
            var diagnostics = stdErr.ToString();
            Assert.Contains("Heartbeat join failed", diagnostics, StringComparison.Ordinal);
            Assert.Contains(nameof(HeartbeatJoinFailureException), diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(nameof(HeartbeatCancellationCallbackException), diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(heartbeatFault.Message, diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalErr);
            registration.Dispose();
            heartbeatGate.TrySetResult();
            var cancellationFailure = await CancelForTeardownAsync(loopCts);
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, cancellationFailure,
                ("controlled heartbeat task", controlledHeartbeatTask), ("RunAsync", run));
        }
    }

    /// <summary>
    /// THE HEARTBEAT TICK'S DIAGNOSTIC IS GUARDED: a degraded <c>Console.Error</c> cannot turn a
    /// swallow-and-retry heartbeat fault into a propagating failure. The REAL factored tick is
    /// driven directly (no timer tick is awaited) against a retired connection, which is the
    /// existing non-cancellation failure path, while the diagnostic sink throws on every write.
    /// <para>
    /// REMOVAL PROOF. Without the guard, the sink's throw escapes <c>SendHeartbeatAsync</c>, so the
    /// awaited tick faults and this assertion fails by name — which is exactly how the production
    /// heartbeat loop would fault and hand teardown a join failure.
    /// </para>
    /// </summary>
    [Fact]
    public async Task HeartbeatTick_WithFailingDiagnostics_StillSwallowsTheFaultAndDoesNotThrow()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse { Accepted = true });
        var service = BuildService(new ProvisionerCapturingRunner(), new ProvisionerHarness().Provisioner);
        var connection = PublishFakeClientConnection(service, invoker, assignedId: AssignedWorkerId);

        // RETIRED: the tick's checked access fails with a non-cancellation error, which is the
        // existing sanitized-log-and-continue path.
        connection.Retire();

        var originalErr = Console.Error;
        var throwingWriter = new ThrowingErrorWriter();
        var tick = Task.CompletedTask;
        try
        {
            Console.SetError(throwingWriter);

            // The guarded diagnostic must swallow the sink's throw: the tick completes normally.
            tick = InvokeHeartbeatTickAsync(service, connection);
            await tick.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.True(throwingWriter.WriteAttempts > 0, "The heartbeat diagnostic must be attempted.");
            // ...and no heartbeat RPC was issued for the retired connection.
            Assert.Equal(0, invoker.HeartbeatCalls);
        }
        finally
        {
            Console.SetError(originalErr);
            await JoinAllForTeardownAsync(service, ("heartbeat tick", tick));
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // (2) Focused fake-client connection tests.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A FAILING DIAGNOSTIC cannot prevent the heartbeat teardown and cannot replace the primary
    /// failure. This is the logger-failure cell of the RunAsync error-precedence rule: with a
    /// PRIMARY transport failure AND a secondary heartbeat-cancellation-callback failure BOTH in
    /// play, the guarded sanitized report itself throws because <c>Console.Error</c> has been
    /// replaced by a writer that throws on every write. The cleanup must still complete — the
    /// ORIGINAL heartbeat task joins, its source is disposed, the transport is disposed — and the
    /// PRIMARY propagates with its ORIGINAL identity, never replaced by the diagnostic failure.
    /// <para>
    /// REMOVAL PROOF. Without the guard around the diagnostic write, the throwing log call inside
    /// <c>PropagateOrReport</c> unwinds the cleanup's <c>finally</c> and REPLACES the propagating
    /// primary: the surfaced exception would be the injected diagnostic failure, so
    /// <c>Assert.Same</c> on the primary instance fails by name.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunAsync_PrimaryFailureWithFailingDiagnostics_JoinsHeartbeatAndPrimarySurfaces()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse
        {
            Accepted = true,
            AssignedWorkerId = AssignedWorkerId,
        });
        var runner = new ProvisionerCapturingRunner();
        var service = BuildService(runner, new ProvisionerHarness().Provisioner);

        // The PRIMARY failure: the initial Ready write fails, inside RunAsync's covered body.
        var primaryFailure = new PrimaryTransportFailureException("primary transport failure");

        var requests = new RecordingRequestStream { FailNextReadyWrite = primaryFailure };
        var responses = new ChannelResponseReader();
        var streamDisposals = 0;
        var stream = new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
            requests, responses,
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ => Interlocked.Increment(ref streamDisposals),
            null!);

        var heartbeatEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeatGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeatFactoryCalls = 0;
        var heartbeatJoinFailure = new HeartbeatJoinFailureException("controlled heartbeat join failure");
        CancellationTokenSource? ownedHeartbeatCts = null;
        Task? controlledHeartbeatTask = null;

        // The callback records its invocation AND throws a distinct secondary type.
        var callbackFailure = new HeartbeatCancellationCallbackException(
            "throwing heartbeat cancellation callback");
        var callbackInvoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;

        service.HeartbeatTaskFactory = (_, cts) =>
        {
            Interlocked.Increment(ref heartbeatFactoryCalls);
            ownedHeartbeatCts = cts;
            registration = cts.Token.Register(() =>
            {
                callbackInvoked.TrySetResult();
                throw callbackFailure;
            });
            heartbeatEntered.TrySetResult();
            controlledHeartbeatTask = ControlledHeartbeatAsync();
            return controlledHeartbeatTask;

            // The controlled task parks until released and then faults with unique join evidence.
            async Task ControlledHeartbeatAsync()
            {
                await heartbeatGate.Task;
                throw heartbeatJoinFailure;
            }
        };

        service.CallInvokerFactory = () => invoker;
        service.WorkStreamFactory = (_, _) => stream;

        var originalErr = Console.Error;
        using var loopCts = new CancellationTokenSource();
        Task<WorkerRunOutcome>? run = null;
        try
        {
            run = service.RunAsync(loopCts.Token);

            // The heartbeat was launched at its unchanged launch point BEFORE the primary failure,
            // so the cleanup always has a running heartbeat to join.
            await heartbeatEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var heartbeatCts = Assert.IsAssignableFrom<CancellationTokenSource>(ownedHeartbeatCts);
            var joinedTask = Assert.IsAssignableFrom<Task>(controlledHeartbeatTask);
            Assert.Equal(1, Volatile.Read(ref heartbeatFactoryCalls));

            // From here on the diagnostics sink itself is BROKEN: every write throws. It also
            // signals specifically when the heartbeat-join report is attempted, proving the
            // ORIGINAL controlled task reached the production join boundary.
            var throwingWriter = new ThrowingErrorWriter("Heartbeat join failed");
            Console.SetError(throwingWriter);

            // The secondary failure fired during cleanup; the join must still be parked on the
            // ORIGINAL controlled task — nothing was skipped because the sink is broken.
            await callbackInvoked.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.False(joinedTask.IsCompleted);
            Assert.False(run.IsCompleted, "The join must still occur even though the diagnostics sink is broken.");
            Assert.Equal(0, Volatile.Read(ref streamDisposals));

            // Release the ORIGINAL task. The throwing writer's marker is fired only when production
            // reports this task's distinct join failure, so a skipped join cannot pass post-hoc.
            heartbeatGate.TrySetResult();
            await throwingWriter.MarkerObserved.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // The PRIMARY propagates — unchanged, with its own identity, despite the throwing
            // diagnostic sink inside the guarded report.
            var thrown = await Assert.ThrowsAsync<PrimaryTransportFailureException>(
                () => run.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Same(primaryFailure, thrown);

            // The join happened, the source was disposed, and the transport went away afterwards.
            Assert.True(joinedTask.IsFaulted, "The task the factory returned must have been joined.");
            Assert.Throws<ObjectDisposedException>(() => _ = heartbeatCts.Token);
            Assert.Equal(1, Volatile.Read(ref streamDisposals));
            Assert.True(throwingWriter.WriteAttempts > 0, "The guarded diagnostic must be attempted.");
        }
        finally
        {
            Console.SetError(originalErr);
            registration.Dispose();
            heartbeatGate.TrySetResult();
            var cancellationFailure = await CancelForTeardownAsync(loopCts);
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, cancellationFailure,
                ("controlled heartbeat task", controlledHeartbeatTask), ("RunAsync", run));
        }
    }

    /// <summary>
    /// A diagnostic sink that throws on EVERY write — modelling a broken or closed
    /// <c>Console.Error</c>. Used to prove the guarded sanitized report cannot skip cleanup or
    /// replace the primary failure: without the production guard the throw from this writer
    /// unwinds the cleanup's finally.
    /// </summary>
    private sealed class PrimaryTransportFailureException(string message) : Exception(message);

    private sealed class HeartbeatCancellationCallbackException(string message) : Exception(message);

    private sealed class HeartbeatJoinFailureException(string message) : Exception(message);

    /// <summary>
    /// Records all diagnostics and signals when a specific production report is written. The marker
    /// is the positive boundary used by controlled-heartbeat tests: it can only be emitted after
    /// RunAsync awaited and classified the ORIGINAL heartbeat task's fault.
    /// </summary>
    private sealed class MarkerObservingWriter(string marker, System.IO.TextWriter inner)
        : System.IO.TextWriter
    {
        private readonly TaskCompletionSource _markerObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        internal Task MarkerObserved => _markerObserved.Task;

        public override void WriteLine(string? value)
        {
            inner.WriteLine(value);
            Observe(value);
        }

        public override void Write(string? value)
        {
            inner.Write(value);
            Observe(value);
        }

        public override void Write(char value) => inner.Write(value);

        private void Observe(string? value)
        {
            if (value is not null && value.Contains(marker, StringComparison.Ordinal))
                _markerObserved.TrySetResult();
        }
    }

    private sealed class ThrowingErrorWriter(string? marker = null) : System.IO.TextWriter
    {
        private readonly TaskCompletionSource _markerObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writeAttempts;

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        internal int WriteAttempts => Volatile.Read(ref _writeAttempts);

        internal Task MarkerObserved => _markerObserved.Task;

        public override void Write(char value) => Throw(value.ToString());

        public override void Write(string? value) => Throw(value);

        public override void WriteLine(string? value) => Throw(value);

        private void Throw(string? value)
        {
            Interlocked.Increment(ref _writeAttempts);
            if (marker is not null && value is not null && value.Contains(marker, StringComparison.Ordinal))
                _markerObserved.TrySetResult();
            throw new InvalidOperationException("injected diagnostic failure");
        }
    }

    /// <summary>
    /// SESSION LOAD and SAVE go through the CONNECTION's own client, carrying the exact arguments
    /// the caller supplied, and a not-found response loads as <c>null</c>.
    /// </summary>
    [Fact]
    public async Task SessionLoadAndSave_UseTheConnectionClient_WithExactArguments()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse { Accepted = true });
        invoker.SessionToReturn = new GetSessionResponse { Found = true, SessionJson = "{\"turn\":7}" };
        var service = BuildService(new ProvisionerCapturingRunner(), new ProvisionerHarness().Provisioner);

        // The service is disposed by the teardown helper, never by a `using` declaration:
        // disposal must not race ahead of the joins below, and a still-live producer must
        // surface as a loud named failure instead of being disposed out from under.
        try
        {
            var connection = PublishFakeClientConnection(service, invoker);

            var loaded = await service.GetSessionAsync("goal-1:coder", TestContext.Current.CancellationToken);
            Assert.Equal("{\"turn\":7}", loaded);
            Assert.Equal(1, invoker.GetSessionCalls);
            Assert.Equal("goal-1:coder", invoker.LastGetSessionId);

            await service.SaveSessionAsync("goal-1:coder", "{\"turn\":8}", TestContext.Current.CancellationToken);
            Assert.Equal(1, invoker.SaveSessionCalls);
            Assert.Equal("goal-1:coder", invoker.LastSaveSessionId);
            Assert.Equal("{\"turn\":8}", invoker.LastSaveSessionJson);

            // A not-found response loads as null — absence is not an error.
            invoker.SessionToReturn = new GetSessionResponse { Found = false };
            Assert.Null(await service.GetSessionAsync("missing:role", TestContext.Current.CancellationToken));

            // Both RPCs went through THIS connection's client (one client per connection).
            Assert.NotNull(connection.Client);
            Assert.Equal(2, invoker.GetSessionCalls);
        }
        finally
        {
            await JoinAllForTeardownAsync(service, priorFailure: null);
        }
    }

    /// <summary>
    /// PROVISIONING TOKEN FORWARDING: the production provisioner the connection builds carries the
    /// connection's ASSIGNED identity into the request, and forwards the provisioned token out of
    /// the fake response (tracked in memory for the config-repo credential chain) — never read from
    /// any operator environment.
    /// </summary>
    [Fact]
    public async Task ProductionProvisioner_ForwardsAssignedIdentityAndProvisionedToken()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse { Accepted = true });
        invoker.WorkerConfigToReturn = new GetWorkerConfigResponse
        {
            GithubToken = "ghp_provisioned_by_orchestrator",
            LlmProvider = "copilot",
        };

        var service = BuildService(new ProvisionerCapturingRunner(), new ProvisionerHarness().Provisioner);

        // The service is disposed by the teardown helper, never by a `using` declaration:
        // disposal must not race ahead of the joins below, and a still-live producer must
        // surface as a loud named failure instead of being disposed out from under.
        try
        {
            var connection = PublishFakeClientConnection(
                service, invoker, assignedId: AssignedWorkerId, productionProvisioner: true);

            // A NULL provisioner override, so the connection built the PRODUCTION provisioner.
            Assert.NotNull(connection.Provisioner);
            await connection.Provisioner!.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);

            Assert.Equal(1, invoker.WorkerConfigCalls);
            Assert.Equal(AssignedWorkerId, invoker.LastWorkerConfigWorkerId);

            // The provisioned token is forwarded out of the response.
            Assert.Equal("ghp_provisioned_by_orchestrator", connection.Provisioner.ResolveConfigRepoCredential());
        }
        finally
        {
            await JoinAllForTeardownAsync(service, priorFailure: null);
        }
    }

    /// <summary>
    /// The connection's PRODUCTION provisioning path is retirement-gated: a fetch after retirement
    /// fails with the EXISTING disconnected error and starts NO transport, while a fetch that
    /// already passed the check keeps its captured client and outcome.
    /// </summary>
    [Fact]
    public async Task ProductionProvisioningPath_FailsDisconnectedAfterRetirement_WithoutTransport()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse { Accepted = true });
        var service = BuildService(new ProvisionerCapturingRunner(), new ProvisionerHarness().Provisioner);

        // The service is disposed by the teardown helper, never by a `using` declaration:
        // disposal must not race ahead of the joins below, and a still-live producer must
        // surface as a loud named failure instead of being disposed out from under.
        try
        {
            var connection = PublishFakeClientConnection(
                service, invoker, assignedId: AssignedWorkerId, productionProvisioner: true);

            // A live fetch reaches the fake client once and carries the ASSIGNED identity.
            await connection.Provisioner!.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);
            Assert.Equal(1, invoker.WorkerConfigCalls);
            Assert.Equal(AssignedWorkerId, invoker.LastWorkerConfigWorkerId);

            // A NEW fetch after retirement fails disconnected BEFORE starting transport.
            connection.Retire();
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => connection.Provisioner!.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, failure.Message);
            Assert.Equal(1, invoker.WorkerConfigCalls);
        }
        finally
        {
            await JoinAllForTeardownAsync(service, priorFailure: null);
        }
    }

    /// <summary>
    /// RETIRED-ENTRY REJECTION: every connection entry point refuses a retired connection with the
    /// EXISTING disconnected error, and issues NO transport — the provisioning fetch, the session
    /// RPCs and the bridge send all fail before reaching the client or the stream.
    /// </summary>
    [Fact]
    public async Task RetiredConnection_RejectsEveryEntryPoint_WithoutStartingTransport()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse { Accepted = true });
        var requests = new RecordingRequestStream();
        var service = BuildService(new ProvisionerCapturingRunner(), new ProvisionerHarness().Provisioner);

        // The service is disposed by the teardown helper, never by a `using` declaration:
        // disposal must not race ahead of the joins below, and a still-live producer must
        // surface as a loud named failure instead of being disposed out from under.
        try
        {
            var connection = PublishFakeClientConnection(service, invoker, writer: requests);

            connection.Retire();

            var ensure = Assert.Throws<InvalidOperationException>(() => connection.EnsureUsable());
            Assert.Equal(WorkerConnection.DisconnectedMessage, ensure.Message);

            var fetch = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.FetchWorkerConfigAsync(
                new GetWorkerConfigRequest { WorkerId = connection.AssignedId },
                TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, fetch.Message);

            var load = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.GetSessionAsync("goal:role", TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, load.Message);

            var save = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.SaveSessionAsync("goal:role", "{}", TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, save.Message);

            var send = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ReportNarrativeAsync("t", "n", TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, send.Message);

            // NO transport was ever started for the retired connection.
            Assert.Equal(0, invoker.GetSessionCalls);
            Assert.Equal(0, invoker.SaveSessionCalls);
            Assert.Equal(0, invoker.WorkerConfigCalls);
            Assert.Empty(requests.Writes);
        }
        finally
        {
            await JoinAllForTeardownAsync(service, priorFailure: null);
        }
    }

    /// <summary>
    /// HEARTBEAT ARGUMENT FORWARDING, driven through the factored ONE-TICK sender — no 30-second
    /// tick is awaited anywhere. The tick snapshots the connection's identity and the worker's
    /// current task state, and a RETIRED connection issues no heartbeat at all.
    /// </summary>
    [Fact]
    public async Task HeartbeatTick_ForwardsConnectionIdentityAndTaskState_AndSkipsWhenRetired()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse { Accepted = true });
        var service = BuildService(new ProvisionerCapturingRunner(), new ProvisionerHarness().Provisioner);

        // The service is disposed by the teardown helper, never by a `using` declaration:
        // disposal must not race ahead of the joins below, and a still-live producer must
        // surface as a loud named failure instead of being disposed out from under.
        try
        {
            var connection = PublishFakeClientConnection(service, invoker, assignedId: AssignedWorkerId);

            // Idle: no current task, so Busy is false and the state fields are empty.
            await InvokeHeartbeatTickAsync(service, connection);
            Assert.Equal(1, invoker.HeartbeatCalls);
            Assert.Equal(AssignedWorkerId, invoker.LastHeartbeatWorkerId);
            Assert.False(invoker.LastHeartbeatBusy);
            Assert.Equal(string.Empty, invoker.LastHeartbeatTaskId);
            Assert.Equal(string.Empty, invoker.LastHeartbeatRole);
            Assert.Equal(0, invoker.LastHeartbeatContextUsage);

            // Busy: the tick reflects the task state AT the tick.
            SetCurrentTaskState(service, taskId: "task-hb", role: "coder");
            await InvokeHeartbeatTickAsync(service, connection);
            Assert.Equal(2, invoker.HeartbeatCalls);
            Assert.Equal(AssignedWorkerId, invoker.LastHeartbeatWorkerId);
            Assert.True(invoker.LastHeartbeatBusy);
            Assert.Equal("task-hb", invoker.LastHeartbeatTaskId);
            Assert.Equal("coder", invoker.LastHeartbeatRole);

            // RETIRED: checked access comes first, so no heartbeat RPC is issued at all.
            connection.Retire();
            await InvokeHeartbeatTickAsync(service, connection);
            Assert.Equal(2, invoker.HeartbeatCalls);
        }
        finally
        {
            await JoinAllForTeardownAsync(service, priorFailure: null);
        }
    }

    /// <summary>
    /// A send that was ALREADY QUEUED on the send gate when the connection retired cannot write on
    /// it — nor on a replacement. Retirement is checked AFTER the gate is acquired, so the queued
    /// send fails with the EXISTING disconnected error and writes nothing, while the write that held
    /// the gate still completes normally.
    /// <para>
    /// The contender's arrival is proven by the production gate's WAITER QUEUE, not by scheduling:
    /// the holder's write stays parked until the contender is observably waiting at the boundary.
    /// </para>
    /// </summary>
    [Fact]
    public async Task QueuedSend_ChecksRetirementAfterGateAcquisition_AndWritesNothing()
    {
        var gated = new GatedOverlapDetectingRequestStream();
        var service = new WorkerService("http://localhost:9999", LocalWorkerId, ["coder"]);
        var connection = PublishStreamConnection(service, AssignedWorkerId, gated);

        Task? holder = null;
        Task? queued = null;
        try
        {
            // The holder ENTERS the writer and parks there, holding the gate's only permit.
            holder = service.ReportProgressAsync("task-1", "running", "holder", CancellationToken.None);
            await gated.WaitForWriteEnteredAsync(0, TestContext.Current.CancellationToken);

            // The queued send is started while the gate is held, so it cannot yet have run.
            queued = service.ReportNarrativeAsync("task-1", "queued", CancellationToken.None);

            // ARRIVAL EVIDENCE: the queued send is provably PARKED AT THE BOUNDARY before retirement.
            await WaitForSendGateWaitersAsync(service, 1, TestContext.Current.CancellationToken);
            Assert.Equal(1, gated.EnteredWriteCount);

            // Retire the connection WHILE the queued send is still waiting for the gate.
            connection.Retire();

            // Release the holder: it completes, returns the permit, and the queued send then acquires
            // the gate — where it must observe retirement rather than writing.
            gated.ReleaseCurrentWrite();
            await holder.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => queued.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, failure.Message);

            // Exactly ONE write ever entered: the holder's. The queued send wrote nothing.
            Assert.Equal(1, gated.EnteredWriteCount);
            Assert.False(gated.OverlapDetected);
        }
        finally
        {
            gated.EnterTeardownMode();
            gated.ReleaseAllParkedWrites();
            await JoinAllForTeardownAsync(service, ("holder send", holder), ("queued send", queued));
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // (3) One controlled A/B publication test.
    // ══════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// ONE CONTROLLED A/B PUBLICATION TEST, with the SAME worker id on both sides.
    /// <para>
    /// (a) A's cleanup cannot clear B: A is replaced by B by REFERENCE IDENTITY, and A's own
    /// unpublish (the REAL production teardown transition) is then a no-op — B stays published.
    /// </para>
    /// <para>
    /// (b) An A send parked behind the send gate cannot reroute or mix identity: while A's write
    /// holds the gate, the contender is proven PARKED AT THE BOUNDARY (waiter-queue arrival
    /// evidence), B is published, and only then is A's write released. The contender still writes
    /// A's identity on A's stream — B's stream sees nothing at all.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AbPublication_SameWorkerId_ACleanupCannotClearB_AndParkedASendKeepsAIdentity()
    {
        const string SharedWorkerId = "worker-shared";

        var requestsA = new RecordingRequestStream();
        var gatedA = new GatedOverlapDetectingRequestStream();
        var requestsB = new RecordingRequestStream();

        var service = new WorkerService("http://localhost:9999", SharedWorkerId, ["coder"]);

        var connectionA = PublishStreamConnection(service, SharedWorkerId, gatedA);
        var connectionB = PublishStreamConnection(service, SharedWorkerId, requestsB);

        // (a) A's cleanup cannot clear B. The identical worker id on both sides is exactly what
        // would make an ID-keyed clear go wrong; reference identity is what makes it safe.
        Assert.Same(connectionB, GetPublishedConnection(service));
        Assert.Same(connectionA, connectionA.EnsureUsable());
        Unpublish(service, connectionA);
        Assert.Same(connectionB, GetPublishedConnection(service));

        // Re-publish A so the interleaving below starts from A; B then replaces it mid-flight.
        Publish(service, connectionA);
        Assert.Same(connectionA, GetPublishedConnection(service));

        Task? holder = null;
        Task? contender = null;
        try
        {
            // A's send enters A's stream and parks there, HOLDING the gate's only permit.
            holder = service.ReportProgressAsync(
                "task-a", "running", "from-a", CancellationToken.None);
            await gatedA.WaitForWriteEnteredAsync(0, TestContext.Current.CancellationToken);
            Assert.Equal(1, gatedA.EnteredWriteCount);

            // The contender snapshots A (the connection published NOW) and then parks on the gate.
            contender = service.ReportNarrativeAsync("task-a", "from-a-contender", CancellationToken.None);

            // ARRIVAL EVIDENCE: the contender is provably PARKED AT THE SEND BOUNDARY — a bypassed
            // gate never enrols a waiter, so this fails by name instead of passing silently.
            await WaitForSendGateWaitersAsync(service, 1, TestContext.Current.CancellationToken);
            Assert.Equal(1, gatedA.EnteredWriteCount); // it has written NOTHING yet.

            // B replaces A while the contender is still parked.
            Publish(service, connectionB);
            Assert.Same(connectionB, GetPublishedConnection(service));

            // Release A's write: it completes, returns the permit, and the contender acquires it.
            gatedA.ReleaseCurrentWrite();
            await holder.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            await gatedA.WaitForWriteEnteredAsync(1, TestContext.Current.CancellationToken);
            gatedA.ReleaseCurrentWrite();
            await contender.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // NO REROUTE and NO IDENTITY MIXING: both writes landed on A's stream carrying A's own
            // snapshot identity, and B's stream saw nothing at all.
            Assert.False(gatedA.OverlapDetected);
            Assert.Equal(2, gatedA.EnteredWriteCount);
            Assert.Equal(
                ["report_progress", "report_narrative"],
                gatedA.Writes.Select(w => w.ToolRequest.ToolName));
            Assert.All(gatedA.Writes, w => Assert.Equal(SharedWorkerId, w.WorkerId));
            Assert.Empty(requestsB.Writes);
        }
        finally
        {
            // Guaranteed release + join: nothing outlives this finally, even after a failure.
            gatedA.EnterTeardownMode();
            gatedA.ReleaseAllParkedWrites();
            await JoinAllForTeardownAsync(service, ("holder send", holder), ("contender send", contender));
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // (4) The service / runner lifetime guard: ONE run at a time, ONE disposal.
    // ══════════════════════════════════════════════════════════════════════════
    //
    // The production contract these pin: the service owns ONE readonly agent runner, claims a single
    // Idle → Running guard BEFORE preparation, releases it back to Idle in the OUTERMOST finally —
    // after every invocation resource, including the lexical transport disposal — and claims one
    // terminal Idle → Disposed in Dispose. The runner is NEVER replaced, and its provisioning
    // callback is DETACHED after quiescence and before the release.

    /// <summary>
    /// AN OVERLAPPING RUN IS REFUSED BEFORE IT TOUCHES ANYTHING. Run 1 is held INSIDE its preparation
    /// (the gate is entered, so it provably owns the guard), and a second invocation must fail fast
    /// with the EXISTING <see cref="InvalidOperationException"/> category — not by reaching the runner
    /// a second time.
    /// </summary>
    [Fact]
    public async Task RunAsync_OverlappingRun_FailsFastWithoutTouchingTheRunner()
    {
        var runner = new LifetimeProbeRunner
        {
            ConnectGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var service = BuildServiceWithRunner(runner);
        service.CallInvokerFactory = () => new FakeOrchestratorInvoker(new RegisterResponse { Accepted = false });

        using var loopCts = new CancellationTokenSource();
        Task<WorkerRunOutcome>? run = null;
        try
        {
            run = service.RunAsync(loopCts.Token);

            // PRODUCER-START EVIDENCE: run 1 is inside `ConnectAsync`, i.e. it has claimed the guard
            // and is preparing the runner. Its gate is still shut.
            await runner.ConnectEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, runner.ConnectCalls);
            Assert.False(run.IsCompleted);

            // THE OVERLAP IS REFUSED — and the refused call never prepared the runner.
            var overlapping = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.RunAsync(TestContext.Current.CancellationToken));
            Assert.Contains("already in progress", overlapping.Message, StringComparison.Ordinal);
            Assert.Equal(1, runner.ConnectCalls);
            Assert.Equal(0, runner.DisposeCalls);
            Assert.Empty(runner.ProvisionerHistory);

            // The refused call changed nothing about run 1, which then completes normally.
            runner.ConnectGate.TrySetResult(true);
            Assert.Equal(
                WorkerRunOutcome.RegistrationRejected,
                await run.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
        }
        finally
        {
            await loopCts.CancelAsync();
            await JoinAllForTeardownAsync(service, ("run", run));
        }
    }

    /// <summary>
    /// DISPOSE WHILE A RUN IS IN FLIGHT REFUSES, WITHOUT CHANGING STATE AND WITHOUT DISPOSING THE
    /// RUNNER. The in-flight run is provably undisturbed (its gate can still complete it), the runner
    /// is not disposed underneath it, and the service stays usable for that run.
    /// </summary>
    [Fact]
    public async Task Dispose_WhileRunInFlight_RefusesWithoutStateChangeOrRunnerDisposal()
    {
        var runner = new LifetimeProbeRunner
        {
            ConnectGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var service = BuildServiceWithRunner(runner);
        service.CallInvokerFactory = () => new FakeOrchestratorInvoker(new RegisterResponse { Accepted = false });

        using var loopCts = new CancellationTokenSource();
        Task<WorkerRunOutcome>? run = null;
        try
        {
            run = service.RunAsync(loopCts.Token);
            await runner.ConnectEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // REFUSED: the caller must cancel/await the run first. The runner is NOT touched.
            var refusal = Assert.Throws<InvalidOperationException>(service.Dispose);
            Assert.Contains("run is in progress", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(0, runner.DisposeCalls);

            // ...and the refusal changed NO state: the run is still owned by the guard, so a second
            // overlapping run is still refused and the held run still completes normally.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.RunAsync(TestContext.Current.CancellationToken));
            Assert.False(run.IsCompleted, "The in-flight run must be untouched by the refused disposal.");

            runner.ConnectGate.TrySetResult(true);
            Assert.Equal(
                WorkerRunOutcome.RegistrationRejected,
                await run.WaitAsync(Failsafe, TestContext.Current.CancellationToken));

            // ONCE THE RUN'S RESOURCES HAVE ENDED the same service is still usable: the guard returned
            // to Idle, and a further SEQUENTIAL run is admitted on the SAME runner instance.
            Assert.Equal(
                WorkerRunOutcome.RegistrationRejected,
                await service.RunAsync(TestContext.Current.CancellationToken));
            Assert.Equal(2, runner.ConnectCalls);
            Assert.Equal(0, runner.DisposeCalls);
        }
        finally
        {
            await loopCts.CancelAsync();
            await JoinAllForTeardownAsync(service, ("run", run));
        }
    }

    /// <summary>
    /// AN ACCEPTED RUN THAT ENDS LEAVES THE SERVICE REUSABLE: a SECOND sequential run is admitted on
    /// the SAME service and the SAME runner instance, and each run's provisioning callback is
    /// installed for it and detached at its own quiescence — in that order, once per run.
    /// </summary>
    /// <remarks>
    /// The ordered <c>ProvisionerHistory</c> is what makes detachment observable: an install that is
    /// never detached would leave the history at <c>["callback", "callback"]</c>, and run 2's own
    /// install could then be clobbered by run 1's late teardown. This is the SERVICE-level reuse
    /// proof; the process-level retry disposition belongs to the attempt loop.
    /// </remarks>
    [Fact]
    public async Task RunAsync_AcceptedRunEnds_AnotherSequentialRunIsAllowedOnTheSameRunner()
    {
        const string RunAId = "worker-run-a";
        const string RunBId = "worker-run-b";

        var runner = new LifetimeProbeRunner();
        var service = BuildServiceWithRunner(runner);

        var requestsA = new RecordingRequestStream();
        var responsesA = new ChannelResponseReader();
        var disposalsA = 0;
        var streamA = BuildRunStream(requestsA, responsesA, () => Interlocked.Increment(ref disposalsA));

        var requestsB = new RecordingRequestStream();
        var responsesB = new ChannelResponseReader();
        var disposalsB = 0;
        var streamB = BuildRunStream(requestsB, responsesB, () => Interlocked.Increment(ref disposalsB));

        var invokerA = new FakeOrchestratorInvoker(
            new RegisterResponse { Accepted = true, AssignedWorkerId = RunAId });
        var invokerB = new FakeOrchestratorInvoker(
            new RegisterResponse { Accepted = true, AssignedWorkerId = RunBId });

        service.CallInvokerFactory = () => invokerA;
        service.WorkStreamFactory = (_, _) => streamA;

        using var loopCts = new CancellationTokenSource();
        Task<WorkerRunOutcome> runA = Task.FromResult(WorkerRunOutcome.RegistrationRejected);
        Task<WorkerRunOutcome> runB = Task.FromResult(WorkerRunOutcome.RegistrationRejected);
        try
        {
            // ── RUN 1: accepted registration, controlled EOF ──
            runA = service.RunAsync(loopCts.Token);
            await requestsA.WaitForWriteCountAsync(1, TestContext.Current.CancellationToken);
            Assert.Equal(RunAId, requestsA.Writes[0].WorkerId);
            responsesA.TryComplete();
            Assert.Equal(
                WorkerRunOutcome.WorkStreamEnded,
                await runA.WaitAsync(Failsafe, TestContext.Current.CancellationToken));

            // The lexical transport disposal ran, the connection is gone, and run 1's callback was
            // detached with the EXISTING SetConfigProvisioner(null) call.
            Assert.Equal(1, Volatile.Read(ref disposalsA));
            Assert.Null(GetPublishedConnection(service));
            Assert.Equal(1, runner.ConnectCalls);
            Assert.Equal(new string?[] { "callback", null }, runner.ProvisionerHistory);

            // ── RUN 2 ON THE SAME SERVICE AND THE SAME RUNNER ──
            service.CallInvokerFactory = () => invokerB;
            service.WorkStreamFactory = (_, _) => streamB;

            runB = service.RunAsync(loopCts.Token);
            await requestsB.WaitForWriteCountAsync(1, TestContext.Current.CancellationToken);
            Assert.Equal(RunBId, requestsB.Writes[0].WorkerId);
            responsesB.TryComplete();
            Assert.Equal(
                WorkerRunOutcome.WorkStreamEnded,
                await runB.WaitAsync(Failsafe, TestContext.Current.CancellationToken));

            Assert.Equal(2, runner.ConnectCalls);
            Assert.Equal(1, Volatile.Read(ref disposalsB));
            // INSTALL → DETACH → INSTALL → DETACH: each run installed exactly one callback for its OWN
            // connection and detached it, so no run inherited the other's retired binding.
            Assert.Equal(new string?[] { "callback", null, "callback", null }, runner.ProvisionerHistory);
            // The runner was never disposed (nor recreated) between the runs.
            Assert.Equal(0, runner.DisposeCalls);
        }
        finally
        {
            await loopCts.CancelAsync();
            responsesA.TryComplete();
            responsesB.TryComplete();
            await JoinAllForTeardownAsync(service, ("run A", runA), ("run B", runB));
        }
    }

    /// <summary>
    /// THE GUARD SPANS THE EXISTING JOINS — it is NOT released early, and the callback is detached
    /// only at QUIESCENCE.
    /// <para>
    /// The controlled heartbeat task parks until the test releases it, so teardown is provably inside
    /// its existing heartbeat join. At that instant: the transport is undisposed, the callback is
    /// STILL INSTALLED (no detach), an overlapping run is STILL refused, and disposal is STILL
    /// refused. Only after the join completes is the transport disposed, the callback detached and the
    /// guard released.
    /// </para>
    /// <para>
    /// TEARDOWN ARRIVAL IS ACKNOWLEDGED BY PRODUCTION, NOT ASSUMED. EOF alone says only that the
    /// reader was completed; the run could still be parked inside <c>MoveNext</c> when the assertions
    /// run, which would make them pass even for a guard released right after
    /// <c>ProcessMessagesAsync</c> returned. The test therefore waits for a signal raised from a
    /// cancellation callback registered on THE ACTUAL heartbeat <see cref="CancellationTokenSource"/>
    /// the production teardown owns: that callback can only fire from
    /// <c>CaptureCancellationFailureAsync</c>, i.e. after the message loop returned and immediately
    /// before the heartbeat join — so every assertion below is taken with teardown provably past the
    /// reader and at the join.
    /// </para>
    /// <para>
    /// REMOVAL PROOF. Releasing the guard (or detaching) anywhere earlier than the joins — e.g. right
    /// after the message loop returned — lets the overlapping run in and/or records the detach while
    /// the heartbeat is still held, so the refused-run assertions and the detach-time observations
    /// fail by name. And because the detach callback itself reads the ACTUAL guard field, moving
    /// <c>ReleaseRunGuard</c> before <c>SetConfigProvisioner(null)</c> fails
    /// <c>lifecycleStateAtDetach</c> (and the in-detach refusal) by name too.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunAsync_GuardSpansHeartbeatJoinAndTransportDisposal_DetachingOnlyAtQuiescence()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse
        {
            Accepted = true,
            AssignedWorkerId = AssignedWorkerId,
        });
        var runner = new LifetimeProbeRunner();
        var service = BuildServiceWithRunner(runner);

        var requests = new RecordingRequestStream();
        var responses = new ChannelResponseReader();
        var disposals = 0;
        var transportDisposed = false;
        var stream = BuildRunStream(requests, responses, () =>
        {
            Interlocked.Increment(ref disposals);
            transportDisposed = true;
        });

        // The controlled heartbeat task: parks until released, never observes the token.
        var heartbeatEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeatGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // THE TEARDOWN-ARRIVAL SIGNAL, raised from a callback on the ACTUAL owned heartbeat source.
        // Production cancels that source only AFTER the message loop returned and immediately BEFORE
        // it awaits the heartbeat task, so this is a production-boundary acknowledgement that the run
        // has left the reader and reached the join.
        var teardownReachedJoin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration heartbeatCancellationRegistration = default;
        Task? controlledHeartbeat = null;
        service.HeartbeatTaskFactory = (_, cts) =>
        {
            heartbeatCancellationRegistration = cts.Token.Register(() => teardownReachedJoin.TrySetResult());
            heartbeatEntered.TrySetResult();
            controlledHeartbeat = HeartbeatAsync();
            return controlledHeartbeat;

            async Task HeartbeatAsync() => await heartbeatGate.Task;
        };

        Task<WorkerRunOutcome>? run = null;

        // THE DETACH-TIME OBSERVATION: recorded from INSIDE the production detach call, so it states
        // what was ALREADY true when the runner's callback was cleared.
        var transportDisposedAtDetach = false;
        var heartbeatJoinedAtDetach = false;
        var lifecycleStateAtDetach = -1;
        Exception? competingRunFailureAtDetach = null;
        runner.OnSetConfigProvisioner = provisioner =>
        {
            if (provisioner is not null)
                return;

            transportDisposedAtDetach = transportDisposed;
            heartbeatJoinedAtDetach = controlledHeartbeat?.IsCompleted ?? false;

            // THE GUARD IS STILL HELD AT THE DETACH. Read the ACTUAL guard field, and additionally
            // prove it BEHAVIOURALLY: a competing run started from right here must be refused. The
            // claim is synchronous — ClaimRunGuard runs before RunAsync's first await, so the
            // returned task is already faulted and is fully observed here, never abandoned.
            lifecycleStateAtDetach = ReadLifecycleState(service);
            var competing = service.RunAsync(CancellationToken.None);
            competingRunFailureAtDetach = competing.IsCompleted
                ? competing.Exception?.Flatten().InnerExceptions.FirstOrDefault()
                : new Xunit.Sdk.XunitException(
                    "A competing run started during the detach did not complete synchronously, so the "
                    + "guard claim is no longer a synchronous fail-fast.");
        };

        service.CallInvokerFactory = () => invoker;
        service.WorkStreamFactory = (_, _) => stream;

        using var loopCts = new CancellationTokenSource();
        try
        {
            run = service.RunAsync(loopCts.Token);

            // The initial Ready proves publication through the REAL lifecycle and that the heartbeat
            // was launched at its unchanged point.
            await requests.WaitForWriteCountAsync(1, TestContext.Current.CancellationToken);
            await heartbeatEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // EOF ends the message loop. Teardown then cancels the linked source and PARKS ON THE
            // ORIGINAL heartbeat join — which this test still holds shut.
            responses.TryComplete();

            // BARRIER: production acknowledged the teardown. Without it the assertions below could run
            // while the run was still blocked in the reader, and would then also pass for a guard that
            // is released right after the message loop returns.
            await teardownReachedJoin.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // Nothing has been disposed, nothing detached, and the run has not returned: teardown is
            // parked in the EXISTING join.
            Assert.Equal(0, Volatile.Read(ref disposals));
            Assert.Equal(0, runner.DetachInvoked);
            Assert.False(run.IsCompleted);
            Assert.False(controlledHeartbeat!.IsCompleted, "The heartbeat join must still be held open.");

            // THE GUARD STILL SPANS THE JOIN: an overlapping run and a disposal are BOTH still refused.
            var overlapping = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.RunAsync(TestContext.Current.CancellationToken));
            Assert.Contains("already in progress", overlapping.Message, StringComparison.Ordinal);
            Assert.Throws<InvalidOperationException>(service.Dispose);
            Assert.Equal(1, runner.ConnectCalls);
            Assert.Equal(0, runner.DisposeCalls);

            // Release the ORIGINAL heartbeat task; the join completes and the run finishes normally.
            heartbeatGate.TrySetResult();
            Assert.Equal(
                WorkerRunOutcome.WorkStreamEnded,
                await run.WaitAsync(Failsafe, TestContext.Current.CancellationToken));

            // THE ORDERING: the detach happened AFTER the heartbeat join AND after the lexical
            // transport disposal, and while the run guard was STILL HELD.
            Assert.Equal(1, Volatile.Read(ref disposals));
            Assert.True(transportDisposedAtDetach, "The lexical transport must be disposed before the detach.");
            Assert.True(heartbeatJoinedAtDetach, "The original heartbeat task must be joined before the detach.");

            // DETACH-BEFORE-RELEASE, proven from inside the detach itself: the guard field read
            // Running, and a competing run started at that instant was refused with the existing
            // category. A release moved ahead of the detach makes BOTH of these fail.
            Assert.Equal(GuardRunning, lifecycleStateAtDetach);
            var refusedAtDetach = Assert.IsType<InvalidOperationException>(competingRunFailureAtDetach);
            Assert.Contains("already in progress", refusedAtDetach.Message, StringComparison.Ordinal);

            // ...and the guard is released only afterwards, so the service is usable again.
            Assert.Equal(GuardIdle, ReadLifecycleState(service));
            Assert.Equal(1, runner.DetachInvoked);
            Assert.Equal(new string?[] { "callback", null }, runner.ProvisionerHistory);
        }
        finally
        {
            heartbeatCancellationRegistration.Dispose();
            heartbeatGate.TrySetResult();
            await loopCts.CancelAsync();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("controlled heartbeat task", controlledHeartbeat), ("run", run));
        }
    }

    /// <summary>
    /// A FAULTING LEXICAL TRANSPORT DISPOSAL KEEPS THE GUARD HELD THROUGH THE DISPOSAL ATTEMPT,
    /// PRESERVES THE DISPOSAL FAULT, AND STILL RELEASES FOR THE NEXT RUN.
    /// <para>
    /// Every other vector's stream disposal completes normally, so the faulted lexical-disposal
    /// ordering was untested. Here the stream's disposal callback THROWS as <c>RunCoreAsync</c>
    /// unwinds: the fault becomes the run's primary, the detach — which runs strictly after that
    /// unwinding — observes the guard STILL <c>Running</c> and the disposal ALREADY attempted, the
    /// ORIGINAL disposal exception surfaces from <c>RunAsync</c> with its own identity, and the guard
    /// is nonetheless released so a subsequent run is admitted on the same service and runner.
    /// </para>
    /// <para>
    /// REMOVAL PROOF: a guard released before the detach makes the in-detach state read fail; a
    /// detach placed before the transport disposal makes <c>transportDisposalAttemptedAtDetach</c>
    /// fail; a swallowed disposal fault makes the <c>Assert.Same</c> fail; and a guard not released on
    /// this fault path makes the follow-up run fail with the overlap refusal.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunAsync_ThrowingTransportDisposal_HoldsGuardThroughDisposal_PreservesFaultAndReleases()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse
        {
            Accepted = true,
            AssignedWorkerId = AssignedWorkerId,
        });
        var runner = new LifetimeProbeRunner();
        var service = BuildServiceWithRunner(runner);

        // THE FAULTING LEXICAL DISPOSAL: the attempt is recorded BEFORE the throw, so "the disposal
        // was attempted" stays observable even though it never completes normally.
        var disposalFailure = new TransportDisposalFailureException("transport disposal failed");
        var disposalAttempts = 0;
        var requests = new RecordingRequestStream();
        var responses = new ChannelResponseReader();
        var stream = BuildRunStream(requests, responses, () =>
        {
            Interlocked.Increment(ref disposalAttempts);
            throw disposalFailure;
        });

        // THE DETACH-TIME OBSERVATION, taken from inside the production detach call.
        var lifecycleStateAtDetach = -1;
        var transportDisposalAttemptedAtDetach = 0;
        runner.OnSetConfigProvisioner = provisioner =>
        {
            if (provisioner is null)
            {
                lifecycleStateAtDetach = ReadLifecycleState(service);
                transportDisposalAttemptedAtDetach = Volatile.Read(ref disposalAttempts);
            }
        };

        service.CallInvokerFactory = () => invoker;
        service.WorkStreamFactory = (_, _) => stream;

        using var loopCts = new CancellationTokenSource();
        Task<WorkerRunOutcome>? run = null;
        try
        {
            run = service.RunAsync(loopCts.Token);
            await requests.WaitForWriteCountAsync(1, TestContext.Current.CancellationToken);

            // EOF ends the loop; the lexical transport disposal then throws while unwinding.
            responses.TryComplete();

            // THE ORIGINAL DISPOSAL FAULT SURFACES, unchanged and unwrapped.
            var thrown = await Assert.ThrowsAsync<TransportDisposalFailureException>(
                () => run.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Same(disposalFailure, thrown);
            Assert.Equal(1, Volatile.Read(ref disposalAttempts));

            // THE GUARD WAS STILL HELD THROUGH THE DISPOSAL ATTEMPT: the detach — which runs after the
            // transport unwinding — observed the disposal already attempted AND the guard Running.
            Assert.Equal(1, transportDisposalAttemptedAtDetach);
            Assert.Equal(GuardRunning, lifecycleStateAtDetach);
            Assert.Equal(1, runner.DetachInvoked);
            Assert.Equal(new string?[] { "callback", null }, runner.ProvisionerHistory);

            // ...AND IT WAS RELEASED ANYWAY: a subsequent run is admitted on the SAME service and the
            // SAME runner, which a guard stranded by the faulted disposal would refuse.
            Assert.Equal(GuardIdle, ReadLifecycleState(service));
            service.CallInvokerFactory = () => new FakeOrchestratorInvoker(
                new RegisterResponse { Accepted = false });
            Assert.Equal(
                WorkerRunOutcome.RegistrationRejected,
                await service.RunAsync(TestContext.Current.CancellationToken));
            Assert.Equal(2, runner.ConnectCalls);
            Assert.Equal(0, runner.DisposeCalls);

            // Final disposal still works exactly once afterwards.
            service.Dispose();
            Assert.Equal(1, runner.DisposeCalls);
        }
        finally
        {
            await loopCts.CancelAsync();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, ("run", run));
        }
    }

    /// <summary>The <c>LifecycleIdle</c> guard value, mirrored for readable assertions.</summary>
    private const int GuardIdle = 0;

    /// <summary>The <c>LifecycleRunning</c> guard value, mirrored for readable assertions.</summary>
    private const int GuardRunning = 1;

    /// <summary>The <c>LifecycleDisposed</c> guard value, mirrored for readable assertions.</summary>
    private const int GuardDisposed = 2;

    /// <summary>A distinct failure type for the faulting lexical transport disposal.</summary>
    private sealed class TransportDisposalFailureException(string message) : Exception(message);

    /// <summary>
    /// FINAL DISPOSAL HAPPENS ONCE, AFTER FINAL DISPOSAL THE SERVICE CANNOT RUN AGAIN, and the state
    /// is claimed BEFORE the fallible runner disposal.
    /// </summary>
    [Fact]
    public async Task RunAsync_AfterFinalDisposal_FailsWithObjectDisposedException_AndDisposalHappensOnce()
    {
        var runner = new LifetimeProbeRunner();
        var service = BuildServiceWithRunner(runner);
        service.CallInvokerFactory = () => new FakeOrchestratorInvoker(new RegisterResponse { Accepted = false });

        Assert.Equal(
            WorkerRunOutcome.RegistrationRejected,
            await service.RunAsync(TestContext.Current.CancellationToken));

        service.Dispose();
        Assert.Equal(1, runner.DisposeCalls);

        // A REPEAT DISPOSAL IS A NO-OP: the runner is not interacted with a second time.
        service.Dispose();
        Assert.Equal(1, runner.DisposeCalls);

        // ...and a run after final disposal is refused with the EXISTING .NET disposal category,
        // BEFORE it prepares the runner.
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => service.RunAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, runner.ConnectCalls);
        Assert.Equal(1, runner.DisposeCalls);
    }

    /// <summary>
    /// A THROWING RUNNER DISPOSAL STILL LEAVES THE SERVICE TERMINALLY DISPOSED. The state is claimed
    /// BEFORE the fallible step, so the FIRST call surfaces the ORIGINAL exception unwrapped, a
    /// REPEAT call is a no-op (the original failure is neither re-raised nor replaced by a second
    /// runner interaction), and a run afterwards is refused.
    /// </summary>
    [Fact]
    public async Task Dispose_ThrowingRunnerDisposal_ClaimsBeforeFallibleStep_AndRepeatIsNoOp()
    {
        var disposeFailure = new InvalidOperationException("runner dispose failed");
        var runner = new LifetimeProbeRunner { DisposeFailure = disposeFailure };
        var service = BuildServiceWithRunner(runner);
        service.CallInvokerFactory = () => new FakeOrchestratorInvoker(new RegisterResponse { Accepted = false });

        Assert.Equal(
            WorkerRunOutcome.RegistrationRejected,
            await service.RunAsync(TestContext.Current.CancellationToken));

        var thrown = Assert.Throws<InvalidOperationException>(service.Dispose);
        Assert.Same(disposeFailure, thrown);
        Assert.Equal(1, runner.DisposeCalls);

        // THE ORIGINAL FAILURE IS PRESERVED, not retried: a repeat disposal neither throws nor
        // touches the runner again.
        Assert.Null(Record.Exception(service.Dispose));
        Assert.Equal(1, runner.DisposeCalls);

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => service.RunAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, runner.ConnectCalls);
    }

    /// <summary>
    /// WITH NO PRIOR FAILURE, A DETACHMENT FAILURE PROPAGATES WITH ITS ORIGINAL EVIDENCE — and the
    /// lexical cleanup it is sequenced after has already completed, so the guard is still released and
    /// the service stays reusable.
    /// </summary>
    [Fact]
    public async Task RunAsync_DetachmentFailureWithoutPrimary_PropagatesAndStillReleasesGuard()
    {
        const string RunId = "worker-detach";
        var detachmentFailure = new InvalidOperationException("provisioner detach failed");
        var runner = new LifetimeProbeRunner { DetachFailureOnce = detachmentFailure };
        var service = BuildServiceWithRunner(runner);

        var requests = new RecordingRequestStream();
        var responses = new ChannelResponseReader();
        var disposals = 0;
        var stream = BuildRunStream(requests, responses, () => Interlocked.Increment(ref disposals));
        service.CallInvokerFactory = () =>
            new FakeOrchestratorInvoker(new RegisterResponse { Accepted = true, AssignedWorkerId = RunId });
        service.WorkStreamFactory = (_, _) => stream;

        using var loopCts = new CancellationTokenSource();
        Task<WorkerRunOutcome>? run = null;
        try
        {
            run = service.RunAsync(loopCts.Token);
            await requests.WaitForWriteCountAsync(1, TestContext.Current.CancellationToken);
            responses.TryComplete();

            // THE DETACHMENT FAILURE IS THE AUTHORITATIVE OUTCOME — unchanged, with its own identity.
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => run.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Same(detachmentFailure, thrown);

            // IT DID NOT SKIP THE LEXICAL CLEANUP (which runs strictly earlier): the transport was
            // disposed and the connection retired/unpublished.
            Assert.Equal(1, Volatile.Read(ref disposals));
            Assert.Null(GetPublishedConnection(service));
            Assert.Equal(1, runner.DetachInvoked);

            // ...and the guard was STILL RELEASED, so the service is reusable and disposable.
            service.CallInvokerFactory = () => new FakeOrchestratorInvoker(new RegisterResponse { Accepted = false });
            Assert.Equal(
                WorkerRunOutcome.RegistrationRejected,
                await service.RunAsync(TestContext.Current.CancellationToken));
            Assert.Equal(2, runner.ConnectCalls);
        }
        finally
        {
            await loopCts.CancelAsync();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, ("run", run));
        }
    }

    /// <summary>
    /// WITH A PRIMARY FAILURE ALREADY IN FLIGHT, THE PRIMARY IS AUTHORITATIVE: the detachment failure
    /// is merely REPORTED through the EXISTING guarded sanitized diagnostics (stage marker plus a type
    /// classification — never the message), and the guard is still released.
    /// </summary>
    [Fact]
    public async Task RunAsync_PrimaryFailureWithDetachmentFailure_PreservesPrimaryAndReportsDetachment()
    {
        var primaryFailure = new InvalidOperationException("registration RPC failed");
        var detachmentFailure = new InvalidOperationException("provisioner detach failed");
        var runner = new LifetimeProbeRunner { DetachFailureOnce = detachmentFailure };
        var service = BuildServiceWithRunner(runner);
        service.CallInvokerFactory = () => new FaultingRegisterInvoker(primaryFailure);

        var originalErr = Console.Error;
        var stdErr = new StringWriter();
        try
        {
            Console.SetError(stdErr);

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.RunAsync(TestContext.Current.CancellationToken));
            Assert.Same(primaryFailure, thrown);
            Assert.Equal(1, runner.DetachInvoked);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        var diagnostics = stdErr.ToString();
        Assert.Contains("Provisioner detachment failed", diagnostics, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(detachmentFailure.Message, diagnostics, StringComparison.Ordinal);

        // The guard was released even though BOTH failures occurred, so a later run is admitted.
        service.CallInvokerFactory = () => new FakeOrchestratorInvoker(new RegisterResponse { Accepted = false });
        Assert.Equal(
            WorkerRunOutcome.RegistrationRejected,
            await service.RunAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// THE DETACHMENT REPORT IS GUARDED TOO: a diagnostic sink that THROWS on every write cannot
    /// replace the primary failure, cannot skip the release of the run guard, and cannot corrupt the
    /// service — the marker proves the production report was genuinely attempted.
    /// </summary>
    [Fact]
    public async Task RunAsync_PrimaryFailureWithFailingDetachmentDiagnostics_StillReleasesGuard()
    {
        var primaryFailure = new InvalidOperationException("registration RPC failed");
        var detachmentFailure = new InvalidOperationException("provisioner detach failed");
        var runner = new LifetimeProbeRunner { DetachFailureOnce = detachmentFailure };
        var service = BuildServiceWithRunner(runner);
        service.CallInvokerFactory = () => new FaultingRegisterInvoker(primaryFailure);

        var originalErr = Console.Error;
        var throwingWriter = new ThrowingErrorWriter("Provisioner detachment failed");
        try
        {
            Console.SetError(throwingWriter);

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.RunAsync(TestContext.Current.CancellationToken));
            Assert.Same(primaryFailure, thrown);

            await throwingWriter.MarkerObserved.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(throwingWriter.WriteAttempts > 0);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        // The guarded report could not skip the release, so the service is still usable and disposable.
        service.CallInvokerFactory = () => new FakeOrchestratorInvoker(new RegisterResponse { Accepted = false });
        Assert.Equal(
            WorkerRunOutcome.RegistrationRejected,
            await service.RunAsync(TestContext.Current.CancellationToken));
        service.Dispose();
        Assert.Equal(1, runner.DisposeCalls);
    }

    /// <summary>
    /// A RUN THAT FAILS DURING RUNNER PREPARATION — BEFORE ANY PROVISIONING CALLBACK WAS EVER
    /// INSTALLED AND BEFORE ANY TRANSPORT IS CREATED — STILL DETACHES AND STILL RELEASES THE RUN
    /// GUARD.
    /// <para>
    /// The install-time failure test (<see cref="ThrowingPostPublicationSetup_RetiresAndUnpublishesBeforeStreamDisposal"/>)
    /// covers a setup failure AT the install call itself; this is the EARLIEST cell: the invocation
    /// body faults while PREPARING the runner, before registration and before the work-stream factory
    /// is ever invoked. The only <c>SetConfigProvisioner</c> call must therefore be the <c>null</c>
    /// DETACH, the preparation failure must propagate with its original identity, and the guard must
    /// be released so the service stays reusable and finally disposable.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunAsync_ConnectPreparationFails_StillDetachesAndReleasesGuard_WithNoTransportCreated()
    {
        var preparationFailure = new InvalidOperationException("runner preparation failed");
        var runner = new LifetimeProbeRunner { ConnectFailure = preparationFailure };
        var service = BuildServiceWithRunner(runner);

        // NON-VACUITY CONTROL: on a healthy run this factory IS invoked (the accepted-run tests rely
        // on it), so a zero count below proves THIS body faulted before stream creation, not that the
        // seam is disconnected from the production flow.
        var workStreamFactoryCalls = 0;
        var transportDisposals = 0;
        var stream = BuildRunStream(
            new RecordingRequestStream(), new ChannelResponseReader(),
            () => Interlocked.Increment(ref transportDisposals));
        service.WorkStreamFactory = (_, _) =>
        {
            Interlocked.Increment(ref workStreamFactoryCalls);
            return stream;
        };

        service.CallInvokerFactory = () =>
            new FakeOrchestratorInvoker(new RegisterResponse { Accepted = true, AssignedWorkerId = "prep" });

        var originalErr = Console.Error;
        var stdErr = new StringWriter();
        try
        {
            Console.SetError(stdErr);

            // THE PREPARATION FAILURE PROPAGATES UNCHANGED...
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.RunAsync(TestContext.Current.CancellationToken));
            Assert.Same(preparationFailure, thrown);

            // ...AND THE BODY NEVER REACHED STREAM CREATION: no transport exists to unwind.
            Assert.Equal(1, runner.ConnectCalls);
            Assert.Equal(0, Volatile.Read(ref workStreamFactoryCalls));
            Assert.Equal(0, Volatile.Read(ref transportDisposals));

            // WITH NO CALLBACK EVER INSTALLED, THE ONLY SetConfigProvisioner CALL IS THE DETACH.
            Assert.Equal(new string?[] { null }, runner.ProvisionerHistory);
            Assert.Equal(1, runner.DetachInvoked);

            // THE GUARD WAS RELEASED: the service is reusable and finally disposable.
            runner.ConnectFailure = null;
            service.CallInvokerFactory = () =>
                new FakeOrchestratorInvoker(new RegisterResponse { Accepted = false });
            Assert.Equal(
                WorkerRunOutcome.RegistrationRejected,
                await service.RunAsync(TestContext.Current.CancellationToken));
            service.Dispose();
            Assert.Equal(1, runner.DisposeCalls);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        // A DETACHMENT FAILURE REPORT (guarded diagnostics) IS NOT EXPECTED HERE: the preparation
        // failure is the primary and the detach SUCCEEDED, so the sanitized stderr carries no
        // detachment marker at all.
        Assert.DoesNotContain("Provisioner detachment failed", stdErr.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// ON THE EARLIEST FAILURE PATH, A DETACHMENT FAILURE MUST NOT REPLACE THE PREPARATION FAILURE:
    /// the primary propagates with its original identity, the detach failure is only REPORTED through
    /// the existing guarded sanitized diagnostics, and the run guard is still released so the service
    /// stays reusable and finally disposable.
    /// </summary>
    [Fact]
    public async Task RunAsync_ConnectPreparationFailsWithDetachmentFailure_PreservesPrimaryAndStillReleasesGuard()
    {
        var preparationFailure = new InvalidOperationException("runner preparation failed");
        var detachmentFailure = new InvalidOperationException("provisioner detach failed");
        var runner = new LifetimeProbeRunner
        {
            ConnectFailure = preparationFailure,
            DetachFailureOnce = detachmentFailure,
        };
        var service = BuildServiceWithRunner(runner);

        var originalErr = Console.Error;
        var stdErr = new StringWriter();
        try
        {
            Console.SetError(stdErr);

            // THE PREPARATION FAILURE IS AUTHORITATIVE — SAME INSTANCE, NOT the detach failure.
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.RunAsync(TestContext.Current.CancellationToken));
            Assert.Same(preparationFailure, thrown);
            Assert.Equal(1, runner.DetachInvoked);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        // THE DETACHMENT FAILURE WAS ONLY REPORTED: sanitized marker plus type classification,
        // never the raw message.
        var diagnostics = stdErr.ToString();
        Assert.Contains("Provisioner detachment failed", diagnostics, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(detachmentFailure.Message, diagnostics, StringComparison.Ordinal);

        // THE GUARD WAS STILL RELEASED: another run is admitted and final disposal works.
        runner.ConnectFailure = null;
        runner.DetachFailureOnce = null;
        service.CallInvokerFactory = () =>
            new FakeOrchestratorInvoker(new RegisterResponse { Accepted = false });
        Assert.Equal(
            WorkerRunOutcome.RegistrationRejected,
            await service.RunAsync(TestContext.Current.CancellationToken));
        service.Dispose();
        Assert.Equal(1, runner.DisposeCalls);
    }

    /// <summary>
    /// THE RUN GUARD IS OBSERVABLY <c>Running</c> FOR THE WHOLE BODY AND <c>Idle</c> ONLY AFTER THE
    /// BODY HAS FULLY RETURNED — a structural complement to the behavioral refusal proofs: the guard
    /// field itself is diagnosed while teardown is still parked in the existing heartbeat join, and
    /// it is read as <c>Idle</c> from a caller only after the run returned, with final disposal then
    /// succeeding and post-disposal runs still refused.
    /// <para>
    /// TEARDOWN ARRIVAL IS ACKNOWLEDGED BY PRODUCTION. The parked-teardown read is taken only after a
    /// signal raised from a cancellation callback on THE ACTUAL owned heartbeat
    /// <see cref="CancellationTokenSource"/> — production cancels it after the message loop returned
    /// and immediately before the heartbeat join — so the observation can never be taken while the
    /// run is still blocked in the reader.
    /// </para>
    /// <para>
    /// REMOVAL PROOF: a guard released anywhere before the outermost finally (e.g. right after the
    /// message loop returned) makes the parked-teardown read <c>Idle</c> and fails this test by name.
    /// Without the acknowledgement barrier that same regression could slip through, because the read
    /// might land before the loop had even observed EOF.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunAsync_GuardStateIsRunningWhileTeardownParks_AndIdleOnlyAfterTheRunReturns()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse
        {
            Accepted = true,
            AssignedWorkerId = AssignedWorkerId,
        });
        var runner = new LifetimeProbeRunner();
        var service = BuildServiceWithRunner(runner);

        var requests = new RecordingRequestStream();
        var responses = new ChannelResponseReader();
        var stream = BuildRunStream(requests, responses, () => { });

        // The controlled heartbeat task parks until the test releases it, so the body is provably
        // still inside its existing heartbeat join when the guard is observed.
        var heartbeatEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeatGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // THE TEARDOWN-ARRIVAL SIGNAL: raised from the ACTUAL owned heartbeat source's cancellation,
        // which production requests only after the message loop returned and just before the join.
        var teardownReachedJoin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration heartbeatCancellationRegistration = default;
        Task? controlledHeartbeat = null;
        service.HeartbeatTaskFactory = (_, cts) =>
        {
            heartbeatCancellationRegistration = cts.Token.Register(() => teardownReachedJoin.TrySetResult());
            heartbeatEntered.TrySetResult();
            controlledHeartbeat = HeartbeatAsync();
            return controlledHeartbeat;

            async Task HeartbeatAsync() => await heartbeatGate.Task;
        };

        service.CallInvokerFactory = () => invoker;
        service.WorkStreamFactory = (_, _) => stream;


        using var loopCts = new CancellationTokenSource();
        Task<WorkerRunOutcome>? run = null;
        try
        {
            run = service.RunAsync(loopCts.Token);
            await requests.WaitForWriteCountAsync(1, TestContext.Current.CancellationToken);
            await heartbeatEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // EOF starts teardown, which parks on the heartbeat join this test still holds shut.
            responses.TryComplete();

            // BARRIER: production acknowledged that teardown left the reader and reached the join.
            await teardownReachedJoin.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Equal(0, runner.DetachInvoked);
            Assert.False(run.IsCompleted);
            Assert.False(controlledHeartbeat!.IsCompleted, "The heartbeat join must still be held open.");

            // THE GUARD FIELD ITSELF IS Running WHILE THE BODY IS PARKED IN THE JOIN.
            Assert.Equal(GuardRunning, ReadLifecycleState(service));
            var overlapping = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.RunAsync(TestContext.Current.CancellationToken));
            Assert.Contains("already in progress", overlapping.Message, StringComparison.Ordinal);

            // Release the join; the run finishes and ONLY THEN is the guard observed as Idle.
            heartbeatGate.TrySetResult();
            Assert.Equal(
                WorkerRunOutcome.WorkStreamEnded,
                await run.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Equal(GuardIdle, ReadLifecycleState(service));
            Assert.Equal(1, runner.DetachInvoked);
            Assert.Equal(new string?[] { "callback", null }, runner.ProvisionerHistory);

            // The service is genuinely usable again, and final disposal still works.
            service.CallInvokerFactory = () => new FakeOrchestratorInvoker(
                new RegisterResponse { Accepted = false });
            Assert.Equal(
                WorkerRunOutcome.RegistrationRejected,
                await service.RunAsync(TestContext.Current.CancellationToken));
            Assert.Equal(GuardIdle, ReadLifecycleState(service));
            service.Dispose();
            Assert.Equal(GuardDisposed, ReadLifecycleState(service));

            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => service.RunAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            heartbeatCancellationRegistration.Dispose();
            heartbeatGate.TrySetResult();
            await loopCts.CancelAsync();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("controlled heartbeat task", controlledHeartbeat), ("run", run));
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Harness.
    // ══════════════════════════════════════════════════════════════════════════

    private static OrchestratorMessage Assignment(string taskId) => new()
    {
        Assignment = new TaskAssignment
        {
            TaskId = taskId,
            GoalId = "goal-1",
            GoalDescription = "desc",
            Prompt = "prompt",
            Role = GrpcWorkerRole.Coder,
            Model = FixtureModel,
        },
    };

    /// <summary>
    /// A git handler for a HEALTHY repo whose origin is a credential-free HTTPS URL matching the
    /// provisioned config-repo URL, so the preparation probes, never clones, and never touches the
    /// network.
    /// </summary>
    private static Func<IReadOnlyList<string>, GitProcessResult> HealthyRepoHandler(string configRepoDir) =>
        tokens =>
        {
            if (Matches(tokens, "rev-parse", "--is-inside-work-tree"))
                return new GitProcessResult(0, "true\n", "");
            if (Matches(tokens, "rev-parse", "--show-toplevel"))
                return new GitProcessResult(0, configRepoDir + "\n", "");
            if (Matches(tokens, "remote", "get-url", "origin"))
                return new GitProcessResult(0, "https://github.com/org/config-repo.git\n", "");
            return new GitProcessResult(0, "", "");
        };

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

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"conn-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort: a leaked temp directory must never fail a test.
        }
    }

    /// <summary>An assignment used by the real RunAsync negotiation-to-behavior vectors.</summary>
    private static OrchestratorMessage LifecycleAssignment(string taskId) => new()
    {
        Assignment = new TaskAssignment
        {
            TaskId = taskId,
            GoalId = "goal-registration-gate",
            GoalDescription = "exercise registered completion gate",
            Prompt = "complete through the accepted connection",
            Role = GrpcWorkerRole.Coder,
            Model = FixtureModel,
        },
    };

    /// <summary>A harmless response-loop probe with its own unique request id.</summary>
    private static OrchestratorMessage LifecycleProbe(string requestId) => new()
    {
        ToolResponse = new ToolCallResponse
        {
            RequestId = requestId,
            Success = true,
            ResultJson = "{}",
        },
    };

    private static WorkerService BuildService(
        IAgentRunner runner, WorkerConfigProvisioner provisioner, string configRepoDir = "/config-repo")
    {
        var service = new WorkerService("http://localhost:9999", LocalWorkerId, ["coder"], configRepoDir);
        service.TestProvisioner = provisioner;
        ReplaceRunner(service, runner);
        return service;
    }

    /// <summary>
    /// Builds a service over a supplied runner WITHOUT touching the provisioner seam: the lifecycle
    /// tests exercise the run/disposal guard, not config-repo preparation.
    /// </summary>
    private static WorkerService BuildServiceWithRunner(IAgentRunner runner)
    {
        var service = new WorkerService("http://localhost:9999", LocalWorkerId, ["coder"]);
        ReplaceRunner(service, runner);
        return service;
    }

    /// <summary>
    /// Builds a fake duplex transport for the REAL <see cref="WorkerService.RunAsync"/>, invoking
    /// <paramref name="onDisposed"/> from the stream's disposal callback so a test can observe the
    /// lexical transport disposal as an event.
    /// </summary>
    private static AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> BuildRunStream(
        IClientStreamWriter<WorkerMessage> requests, IAsyncStreamReader<OrchestratorMessage> responses,
        Action onDisposed) =>
        new(
            requests, responses,
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ => onDisposed(),
            null!);

    /// <summary>
    /// The service's CURRENT <c>ActiveAssignment.Execution</c>, or <c>null</c> when the ownership
    /// slot is empty. Observation only — it never mutates production state.
    /// </summary>
    private static Task? TryGetActiveExecution(WorkerService service)
    {
        var active = typeof(WorkerService)
            .GetField("_activeAssignment", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service);
        return active is null
            ? null
            : (Task?)active.GetType().GetProperty("Execution")!.GetValue(active);
    }

    private static void ReplaceRunner(WorkerService service, IAgentRunner runner)
    {        // INSTALL THE REAL-EXECUTION PROBE so the double records the enclosing
        // ActiveAssignment.Execution rather than a completed placeholder.
        if (runner is ProvisionerCapturingRunner capturing)
            capturing.ExecutionProbe = () => TryGetActiveExecution(service);

        var field = typeof(WorkerService).GetField("_agentRunner", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("WorkerService._agentRunner field not found.");
        if (field.GetValue(service) is IAgentRunner existing)
            existing.DisposeAsync().AsTask().GetAwaiter().GetResult();
        field.SetValue(service, runner);
    }

    /// <summary>Reflects the real lifecycle-guard field (observation only — never mutated).</summary>
    private static int ReadLifecycleState(WorkerService service)
    {
        var guardField = typeof(WorkerService)
            .GetField("_lifecycleState", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var boxed = guardField.GetValue(service)!;
        return Volatile.Read(ref Unsafe.Unbox<int>(boxed));
    }
    private static WorkerConnection? GetPublishedConnection(WorkerService service) =>
        (WorkerConnection?)typeof(WorkerService)
            .GetField("_connection", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service);

    /// <summary>Publishes via the REAL publish helper (test-only access; no public reconnect API).</summary>
    private static void Publish(WorkerService service, WorkerConnection connection) =>
        service.PublishConnection(connection);

    /// <summary>
    /// Invokes the REAL unpublish helper — the production teardown transition — so A's cleanup is
    /// exercised exactly as production performs it: by reference identity, never by worker id.
    /// </summary>
    private static void Unpublish(WorkerService service, WorkerConnection expected) =>
        typeof(WorkerService)
            .GetMethod("UnpublishConnection", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [expected]);

    /// <summary>Drives the REAL private message loop with an already-published connection.</summary>
    private static Task InvokeProcessMessages(
        WorkerService service, WorkerConnection connection, CancellationToken ct) =>
        (Task)typeof(WorkerService)
            .GetMethod("ProcessMessagesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [connection, ct])!;

    /// <summary>Invokes the REAL factored one-tick heartbeat sender.</summary>
    private static Task InvokeHeartbeatTickAsync(WorkerService service, WorkerConnection connection) =>
        (Task)typeof(WorkerService)
            .GetMethod("SendHeartbeatAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [connection, TestContext.Current.CancellationToken])!;

    private static void SetCurrentTaskState(WorkerService service, string? taskId, string? role)
    {
        typeof(WorkerService).GetField("_currentTaskId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, taskId);
        typeof(WorkerService).GetField("_currentRole", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, role);
    }

    /// <summary>The production send gate (observation only — never mutated).</summary>
    private static SemaphoreSlim GetSendGate(WorkerService service) =>
        (SemaphoreSlim)typeof(WorkerService)
            .GetField("_sendGate", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service)!;

    private static Task WaitForSendGateWaitersAsync(WorkerService service, int count, CancellationToken ct) =>
        SendGateObserver.WaitForWaitersAsync(GetSendGate(service), count, ct);

    /// <summary>
    /// Builds and publishes a connection whose gRPC CLIENT is materialised from the FAKE INVOKER, so
    /// every unary entry point (session, provisioning, heartbeat) reaches the fake without any live
    /// server. <paramref name="productionProvisioner"/> selects the production provisioner bound to
    /// this connection; leaving it <c>false</c> gives a connection with NO provisioner, which is all
    /// the session / heartbeat / retired-entry tests need.
    /// </summary>
    private static WorkerConnection PublishFakeClientConnection(
        WorkerService service,
        FakeOrchestratorInvoker invoker,
        string assignedId = AssignedWorkerId,
        IClientStreamWriter<WorkerMessage>? writer = null,
        bool productionProvisioner = false)
    {
        var stream = new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
            writer ?? new RecordingRequestStream(),
            new ChannelResponseReader(),
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ => { },
            null!);

        var connection = new WorkerConnection(
            assignedId,
            new HiveOrchestrator.HiveOrchestratorClient(invoker),
            stream,
            provisionerOverride: null,
            includeProductionProvisioner: productionProvisioner);

        service.PublishConnection(connection);
        return connection;
    }

    /// <summary>Publishes a connection over a specific stream writer, returning it.</summary>
    private static WorkerConnection PublishStreamConnection(
        WorkerService service, string assignedId, IClientStreamWriter<WorkerMessage> writer) =>
        PublishFakeClientConnection(
            service, new FakeOrchestratorInvoker(new RegisterResponse()), assignedId, writer);

    /// <summary>
    /// Requests teardown cancellation without allowing a throwing linked-token callback to skip the
    /// response completion and original-task joins that follow in a test's <c>finally</c> block.
    /// </summary>
    private static async Task<Exception?> CancelForTeardownAsync(CancellationTokenSource source)
    {
        try
        {
            await source.CancelAsync();
            return null;
        }
        catch (Exception ex)
        {
            // Defer rather than throw: readers are completed and every original producer is joined
            // before the cancellation-cleanup evidence is rethrown with the other teardown failures.
            return ex;
        }
    }

    /// <summary>
    /// JOINS EVERY ORIGINAL TASK a test started, each under its OWN bounded wait, and only then
    /// reports whatever went wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A test's <c>finally</c> must first release every gate, cancel, and complete/fault every
    /// reader, and then call this ONCE with every started task. Because each join is attempted
    /// independently and failures are accumulated, one still-live producer can never skip the joins
    /// that follow it — which is what previously let a <c>WorkerService</c> be disposed while other
    /// original work was still running.
    /// </para>
    /// <para>
    /// A STILL-LIVE task is a LOUD, distinct failure (never a silent return): it is reported as a
    /// named teardown failure identifying the producer. A task that terminated with a fault or a
    /// cancellation is quiescent, which is all teardown requires, so its outcome is swallowed HERE
    /// ONLY — the real outcome is asserted on the test's normal path.
    /// </para>
    /// </remarks>
    /// <param name="service">The service to dispose only after every known producer is terminal.</param>
    /// <param name="producers">
    /// The started tasks, in the order they should be joined. <c>null</c> entries (a producer a
    /// test never started) are skipped.
    /// </param>
    private static Task JoinAllForTeardownAsync(
        WorkerService service,
        params (string Name, Task? Producer)[] producers) =>
        JoinAllForTeardownAsync(service, priorFailure: null, producers);

    private static async Task JoinAllForTeardownAsync(
        WorkerService service,
        Exception? priorFailure,
        params (string Name, Task? Producer)[] producers)
    {
        List<Exception> failures = priorFailure is null ? [] : [priorFailure];

        // EVERY BODY THAT EVER STARTED, from the runner's own append-only record. A gate dictionary
        // cannot answer "is a late-started invocation still parked?", and the ownership slot only
        // exposes the CURRENT body — so this is the authoritative teardown input.
        var runnerField = typeof(WorkerService)
            .GetField("_agentRunner", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var recordedRunner = runnerField.GetValue(service) as ProvisionerCapturingRunner;
        var startedBodies = recordedRunner?.AssignmentExecutions ?? [];

        for (var index = 0; index < startedBodies.Count; index++)
        {
            var body = startedBodies[index];
            if (!producers.Any(candidate => ReferenceEquals(candidate.Producer, body)))
                producers = [.. producers, ($"recorded assignment execution #{index}", body)];
        }

        // A body can start before an assertion captures it in a local. Discover the service's active
        // original execution after gates/readers were settled and join it independently from its
        // parent loop.
        var active = typeof(WorkerService)
            .GetField("_activeAssignment", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service);
        var activeExecution = active is null
            ? null
            : (Task?)active.GetType().GetProperty("Execution")!.GetValue(active);
        if (activeExecution is not null
            && !producers.Any(candidate => ReferenceEquals(candidate.Producer, activeExecution)))
        {
            producers = [.. producers, ("active assignment body", activeExecution)];
        }

        foreach (var (name, producer) in producers)
        {
            if (producer is not null)
                await JoinOneAsync(name, producer);
        }

        // A buffered assignment can start after the first snapshot while the parent loop is being
        // joined. Re-snapshot and independently join that late body before considering disposal.
        active = typeof(WorkerService)
            .GetField("_activeAssignment", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service);
        var lateActiveExecution = active is null
            ? null
            : (Task?)active.GetType().GetProperty("Execution")!.GetValue(active);
        if (lateActiveExecution is not null
            && !producers.Any(candidate => ReferenceEquals(candidate.Producer, lateActiveExecution)))
        {
            producers = [.. producers, ("late active assignment body", lateActiveExecution)];
            await JoinOneAsync("late active assignment body", lateActiveExecution);
        }

        // FINAL SWEEP of the runner's record: a body may have started while the joins above ran (a
        // buffered assignment the loop only reached during teardown).
        // THE CLOSURE HANDSHAKE. Seal the recording, then drain to a FIXPOINT: join everything
        // recorded, and if an execution was admitted concurrently with (or after) the seal, take
        // the new snapshot and join again. Each join keeps its own bounded wait, so a body that
        // starts during teardown is joined rather than missed.
        recordedRunner?.SealRecording();
        var drainPasses = 0;
        while (recordedRunner is not null)
        {
            var admittedNew = false;
            foreach (var execution in recordedRunner.AssignmentExecutions)
            {
                if (producers.Any(candidate => ReferenceEquals(candidate.Producer, execution)))
                    continue;

                admittedNew = true;
                producers = [.. producers, ($"sealed assignment execution #{drainPasses}", execution)];
                await JoinOneAsync($"sealed assignment execution #{drainPasses}", execution);
            }

            if (!admittedNew && !recordedRunner.RecordedSinceSeal)
                break;

            if (++drainPasses > MaxTeardownDrainPasses)
            {
                failures.Add(new Xunit.Sdk.XunitException(
                    "Teardown could not reach a closed join set: the runner kept admitting new "
                    + "assignment executions after the recording was sealed."));
                break;
            }

            recordedRunner.TakeRecordedSinceSeal();
        }

        // Do not let lexical disposal race a timed-out original task. These tests own service
        // disposal manually: dispose only after every known producer is terminal, otherwise leave
        // it undisposed and surface the named live-work failure below.
        if (producers.All(candidate => candidate.Producer is null || candidate.Producer.IsCompleted))
        {
            try
            {
                service.Dispose();
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (failures.Count == 1)
            throw failures[0];

        if (failures.Count > 1)
            throw new AggregateException("Teardown could not join every original task.", failures);

        async Task JoinOneAsync(string name, Task producer)
        {
            try
            {
                await producer.WaitAsync(Failsafe, CancellationToken.None);
            }
            catch (TimeoutException)
            {
                failures.Add(new Xunit.Sdk.XunitException(
                    $"Teardown failed to join '{name}' within the bounded failsafe; live work remains."));
            }
            catch (Exception) when (producer.IsCompleted)
            {
                // Terminal fault/cancellation: the original task is quiescent.
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }
    }

    /// <summary>
    /// Flattens an exception (and any <see cref="AggregateException"/> wrapper the runtime produced)
    /// so a test can locate the ORIGINAL callback evidence without normalizing it away.
    /// </summary>
    private static IReadOnlyList<Exception> Flatten(Exception exception) =>
        exception is AggregateException aggregate
            ? [.. aggregate.Flatten().InnerExceptions]
            : [exception];

    // ── Fakes ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A <see cref="CallInvoker"/> whose <c>Register</c> RPC always FAULTS, so a test can prove the
    /// registration failure escapes <see cref="WorkerService.RunAsync"/> as an exception rather than
    /// being converted into a returned <see cref="WorkerRunOutcome"/>. Every other unary call is a
    /// fixture bug and throws loudly, and the register call is counted exactly like
    /// <see cref="FakeOrchestratorInvoker"/> counts its calls.
    /// </summary>
    private sealed class FaultingRegisterInvoker(Exception registerFailure) : CallInvoker
    {
        private int _registerCalls;

        internal int RegisterCalls => Volatile.Read(ref _registerCalls);

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException($"Unexpected blocking call {method.FullName}.");

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            var payload = method.FullName switch
            {
                // The register RPC faults; the call still completes as a faulted unary call so the
                // production `await client.RegisterAsync(...)` propagates the ORIGINAL exception.
                "/copilothive.HiveOrchestrator/Register" => FailRegister(),
                "/copilothive.HiveOrchestrator/GetWorkerConfig" => FailUnexpected(),
                "/copilothive.HiveOrchestrator/GetSession" => FailUnexpected(),
                "/copilothive.HiveOrchestrator/SaveSession" => FailUnexpected(),
                "/copilothive.HiveOrchestrator/Heartbeat" => FailUnexpected(),
                _ => FailUnexpected(method.FullName),
            };

            return new AsyncUnaryCall<TResponse>(
                payload,
                Task.FromResult(new Metadata()),
                () => new Status(StatusCode.OK, string.Empty),
                () => new Metadata(),
                () => { });

            Task<TResponse> FailRegister()
            {
                Interlocked.Increment(ref _registerCalls);
                return Task.FromException<TResponse>(registerFailure);
            }

            Task<TResponse> FailUnexpected(string? fullName = null) =>
                Task.FromException<TResponse>(
                    new NotSupportedException($"Unexpected unary call {fullName ?? method.FullName}."));
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException($"Unexpected server-streaming call {method.FullName}.");

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException($"Unexpected client-streaming call {method.FullName}.");

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException($"Unexpected duplex-streaming call {method.FullName}.");
    }

    /// <summary>
    /// A <see cref="CallInvoker"/> answering the unary RPCs the connection boundary reaches —
    /// <c>Register</c>, <c>GetWorkerConfig</c>, <c>GetSession</c>, <c>SaveSession</c> and
    /// <c>Heartbeat</c> — recording every request so a test can assert exact argument forwarding.
    /// Any other call is a fixture bug and throws loudly.
    /// </summary>
    private sealed class FakeOrchestratorInvoker(RegisterResponse registerResponse) : CallInvoker
    {
        private int _registerCalls;
        private int _workerConfigCalls;
        private int _getSessionCalls;
        private int _saveSessionCalls;
        private int _heartbeatCalls;
        private string? _lastRegisterWorkerId;
        private RegisterRequest? _lastRegisterRequest;
        private string? _lastWorkerConfigWorkerId;
        private string? _lastGetSessionId;
        private string? _lastSaveSessionId;
        private string? _lastSaveSessionJson;
        private string? _lastHeartbeatWorkerId;
        private string? _lastHeartbeatTaskId;
        private string? _lastHeartbeatRole;
        private bool _lastHeartbeatBusy;
        private int _lastHeartbeatContextUsage;

        /// <summary>The response the fake <c>GetWorkerConfig</c> returns.</summary>
        internal GetWorkerConfigResponse WorkerConfigToReturn { get; set; } = new();

        /// <summary>The response the fake <c>GetSession</c> returns.</summary>
        internal GetSessionResponse SessionToReturn { get; set; } = new();

        internal int RegisterCalls => Volatile.Read(ref _registerCalls);
        internal int WorkerConfigCalls => Volatile.Read(ref _workerConfigCalls);
        internal int GetSessionCalls => Volatile.Read(ref _getSessionCalls);
        internal int SaveSessionCalls => Volatile.Read(ref _saveSessionCalls);
        internal int HeartbeatCalls => Volatile.Read(ref _heartbeatCalls);

        internal string? LastRegisterWorkerId => Volatile.Read(ref _lastRegisterWorkerId);

        /// <summary>
        /// A CLONE of the exact <see cref="RegisterRequest"/> the production run sent, so a test can
        /// assert the OUTGOING negotiation request rather than any value it supplied itself.
        /// </summary>
        internal RegisterRequest? LastRegisterRequest => Volatile.Read(ref _lastRegisterRequest);
        internal string? LastWorkerConfigWorkerId => Volatile.Read(ref _lastWorkerConfigWorkerId);
        internal string? LastGetSessionId => Volatile.Read(ref _lastGetSessionId);
        internal string? LastSaveSessionId => Volatile.Read(ref _lastSaveSessionId);
        internal string? LastSaveSessionJson => Volatile.Read(ref _lastSaveSessionJson);
        internal string? LastHeartbeatWorkerId => Volatile.Read(ref _lastHeartbeatWorkerId);
        internal string? LastHeartbeatTaskId => Volatile.Read(ref _lastHeartbeatTaskId);
        internal string? LastHeartbeatRole => Volatile.Read(ref _lastHeartbeatRole);
        internal bool LastHeartbeatBusy => Volatile.Read(ref _lastHeartbeatBusy);
        internal int LastHeartbeatContextUsage => Volatile.Read(ref _lastHeartbeatContextUsage);

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException($"Unexpected blocking call {method.FullName}.");

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            var payload = method.FullName switch
            {
                "/copilothive.HiveOrchestrator/Register" => RespondRegister(request),
                "/copilothive.HiveOrchestrator/GetWorkerConfig" => RespondWorkerConfig(request),
                "/copilothive.HiveOrchestrator/GetSession" => RespondGetSession(request),
                "/copilothive.HiveOrchestrator/SaveSession" => RespondSaveSession(request),
                "/copilothive.HiveOrchestrator/Heartbeat" => RespondHeartbeat(request),
                _ => throw new NotSupportedException($"Unexpected unary call {method.FullName}."),
            };

            return new AsyncUnaryCall<TResponse>(
                Task.FromResult((TResponse)payload),
                Task.FromResult(new Metadata()),
                () => new Status(StatusCode.OK, string.Empty),
                () => new Metadata(),
                () => { });
        }

        private object RespondRegister<TRequest>(TRequest request)
        {
            Interlocked.Increment(ref _registerCalls);
            Volatile.Write(ref _lastRegisterWorkerId, (request as RegisterRequest)?.WorkerId);
            Volatile.Write(ref _lastRegisterRequest, (request as RegisterRequest)?.Clone());
            return registerResponse;
        }

        private object RespondWorkerConfig<TRequest>(TRequest request)
        {
            Interlocked.Increment(ref _workerConfigCalls);
            Volatile.Write(ref _lastWorkerConfigWorkerId, (request as GetWorkerConfigRequest)?.WorkerId);
            return WorkerConfigToReturn;
        }

        private object RespondGetSession<TRequest>(TRequest request)
        {
            Interlocked.Increment(ref _getSessionCalls);
            Volatile.Write(ref _lastGetSessionId, (request as GetSessionRequest)?.SessionId);
            return SessionToReturn;
        }

        private object RespondSaveSession<TRequest>(TRequest request)
        {
            Interlocked.Increment(ref _saveSessionCalls);
            if (request is SaveSessionRequest save)
            {
                Volatile.Write(ref _lastSaveSessionId, save.SessionId);
                Volatile.Write(ref _lastSaveSessionJson, save.SessionJson);
            }
            return new SaveSessionResponse { Success = true };
        }

        private object RespondHeartbeat<TRequest>(TRequest request)
        {
            Interlocked.Increment(ref _heartbeatCalls);
            if (request is HeartbeatRequest heartbeat)
            {
                Volatile.Write(ref _lastHeartbeatWorkerId, heartbeat.WorkerId);
                Volatile.Write(ref _lastHeartbeatTaskId, heartbeat.CurrentTaskId);
                Volatile.Write(ref _lastHeartbeatRole, heartbeat.CurrentRole);
                Volatile.Write(ref _lastHeartbeatBusy, heartbeat.Busy);
                Volatile.Write(ref _lastHeartbeatContextUsage, heartbeat.ContextUsagePercent);
            }
            return new HeartbeatResponse { Acknowledged = true };
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException($"Unexpected server-streaming call {method.FullName}.");

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException($"Unexpected client-streaming call {method.FullName}.");

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException(
                $"Unexpected duplex call {method.FullName} — the fixture supplies the stream explicitly.");
    }

    /// <summary>
    /// Records every <see cref="WorkerMessage"/> a stream writes and lets a test await a specific
    /// write or Ready count deterministically. Implements the cancellable write overload
    /// EXPLICITLY, since the default interface method throws for a cancellable token and production
    /// writes with the live stream token.
    /// </summary>
    private sealed class RecordingRequestStream : IClientStreamWriter<WorkerMessage>
    {
        private readonly object _gate = new();
        private readonly List<WorkerMessage> _writes = [];
        private readonly Dictionary<int, TaskCompletionSource> _countWaiters = [];
        private readonly Dictionary<int, TaskCompletionSource> _readyWaiters = [];
        private int _readyCount;

        /// <summary>
        /// Invoked for every write BEFORE it is recorded — the observation point a test uses to
        /// capture production state AT the moment of the write (e.g. whether the connection was
        /// already published).
        /// </summary>
        internal Action<WorkerMessage>? OnWrite { get; set; }

        /// <summary>
        /// ONE-SHOT injected failure for the next <c>WorkerReady</c> write. It models a transport
        /// write that faults INSIDE the covered lifecycle body, so a test can produce a real
        /// PRIMARY <c>RunAsync</c> failure alongside a cancellation-cleanup failure. Consumed on use.
        /// </summary>
        internal Exception? FailNextReadyWrite { get; set; }

        internal IReadOnlyList<WorkerMessage> Writes
        {
            get { lock (_gate) return _writes.ToList(); }
        }

        /// <summary>Completes once at least <paramref name="count"/> writes were recorded.</summary>
        internal Task WaitForWriteCountAsync(int count, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_writes.Count >= count)
                    return Task.CompletedTask;
                if (!_countWaiters.TryGetValue(count, out var waiter))
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _countWaiters[count] = waiter;
                }
                return waiter.Task.WaitAsync(Failsafe, ct);
            }
        }

        /// <summary>Completes once at least <paramref name="count"/> Ready messages were written.</summary>
        internal Task WaitForReadyCountAsync(int count, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_readyCount >= count)
                    return Task.CompletedTask;
                if (!_readyWaiters.TryGetValue(count, out var waiter))
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _readyWaiters[count] = waiter;
                }
                return waiter.Task.WaitAsync(Failsafe, ct);
            }
        }

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(WorkerMessage message)
        {
            Record(message);
            return Task.CompletedTask;
        }

        Task IAsyncStreamWriter<WorkerMessage>.WriteAsync(WorkerMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record(message);
            return ReadyWriteFailure(message);
        }

        public Task CompleteAsync() => Task.CompletedTask;

        /// <summary>
        /// Reports the armed one-shot Ready-write failure AFTER the write was recorded, so the
        /// recorded-write bookkeeping (and any count waiter) still observes the attempt.
        /// </summary>
        private Task ReadyWriteFailure(WorkerMessage message)
        {
            if (message.PayloadCase != WorkerMessage.PayloadOneofCase.Ready || FailNextReadyWrite is not { } failure)
                return Task.CompletedTask;

            FailNextReadyWrite = null;
            return Task.FromException(failure);
        }

        private void Record(WorkerMessage message)
        {
            OnWrite?.Invoke(message);

            List<TaskCompletionSource> ready = [];
            List<int> satisfiedCounts = [];
            List<int> satisfiedReady = [];
            lock (_gate)
            {
                _writes.Add(message);
                if (message.PayloadCase == WorkerMessage.PayloadOneofCase.Ready)
                    _readyCount++;

                foreach (var (threshold, waiter) in _countWaiters)
                {
                    if (_writes.Count >= threshold)
                    {
                        ready.Add(waiter);
                        satisfiedCounts.Add(threshold);
                    }
                }
                foreach (var threshold in satisfiedCounts)
                    _countWaiters.Remove(threshold);

                foreach (var (threshold, waiter) in _readyWaiters)
                {
                    if (_readyCount >= threshold)
                    {
                        ready.Add(waiter);
                        satisfiedReady.Add(threshold);
                    }
                }
                foreach (var threshold in satisfiedReady)
                    _readyWaiters.Remove(threshold);
            }

            foreach (var waiter in ready)
                waiter.TrySetResult();
        }
    }

    /// <summary>
    /// A gated, overlap-detecting request writer: every write records its entry and then parks until
    /// released, so a test can hold the production send gate open and prove where a competing
    /// producer waits. All observables are TCS/counter based — there are NO sleeps.
    /// </summary>
    private sealed class GatedOverlapDetectingRequestStream : FakeClientStreamWriter<WorkerMessage>
    {
        private readonly object _gate = new();
        private readonly List<WorkerMessage> _writes = [];
        private readonly List<TaskCompletionSource> _releaseWaiters = [];
        private readonly List<(int Index, TaskCompletionSource Waiter)> _entryWaiters = [];
        private bool _writeInProgress;
        private bool _overlapped;
        private bool _teardownMode;

        /// <summary>Writes that have ENTERED the fake (started; possibly parked or since completed).</summary>
        internal int EnteredWriteCount { get { lock (_gate) return _writes.Count; } }

        /// <summary>True once any second write entered while another was still parked.</summary>
        internal bool OverlapDetected { get { lock (_gate) return _overlapped; } }

        /// <summary>Snapshot of the messages whose writes COMPLETED, oldest first.</summary>
        internal IReadOnlyList<WorkerMessage> Writes
        {
            get { lock (_gate) return _writes.ToList(); }
        }

        public override Task WriteAsync(WorkerMessage message) => WriteCoreAsync(message, CancellationToken.None);

        protected override Task WriteWithTokenAsync(WorkerMessage message, CancellationToken ct)
            => WriteCoreAsync(message, ct);

        private async Task WriteCoreAsync(WorkerMessage message, CancellationToken ct)
        {
            TaskCompletionSource release;
            lock (_gate)
            {
                if (_writeInProgress)
                    _overlapped = true;
                _writeInProgress = true;

                _writes.Add(message);

                // The release TCS is enqueued BEFORE entry is signalled, so a test that observed
                // entry can always release this write deterministically.
                release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _releaseWaiters.Add(release);
                if (_teardownMode)
                    release.TrySetResult();

                SignalEntryWaiters_Locked();
            }

            try
            {
                await release.Task.WaitAsync(ct);

                lock (_gate)
                {
                    _writeInProgress = false;
                    _releaseWaiters.Remove(release);
                }
            }
            catch
            {
                lock (_gate)
                {
                    _writeInProgress = false;
                    _releaseWaiters.Remove(release);
                }
                throw;
            }
        }

        /// <summary>Completes once the <paramref name="index"/>-th write has ENTERED the fake.</summary>
        internal Task WaitForWriteEnteredAsync(int index, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_writes.Count > index)
                    return Task.CompletedTask;
                var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _entryWaiters.Add((index, waiter));
                return waiter.Task.WaitAsync(Failsafe, ct);
            }
        }

        /// <summary>
        /// Releases the OLDEST currently parked write. Deterministic tests call this only after
        /// <see cref="WaitForWriteEnteredAsync"/> gave producer-start evidence.
        /// </summary>
        internal void ReleaseCurrentWrite()
        {
            TaskCompletionSource waiter;
            lock (_gate)
            {
                if (_releaseWaiters.Count == 0)
                    throw new InvalidOperationException(
                        "No parked write to release — release only after WaitForWriteEnteredAsync evidence.");
                waiter = _releaseWaiters[0];
                _releaseWaiters.RemoveAt(0);
            }
            waiter.TrySetResult();
        }

        /// <summary>Teardown drain: releases every parked write so awaited sends unwind.</summary>
        internal void ReleaseAllParkedWrites()
        {
            lock (_gate)
            {
                foreach (var waiter in _releaseWaiters)
                    waiter.TrySetResult();
                _releaseWaiters.Clear();
            }
        }

        /// <summary>One-way teardown switch: writes that ENTER from now on do not park.</summary>
        internal void EnterTeardownMode()
        {
            lock (_gate)
                _teardownMode = true;
            ReleaseAllParkedWrites();
        }

        public override Task CompleteAsync() => Task.CompletedTask;

        private void SignalEntryWaiters_Locked()
        {
            for (var i = _entryWaiters.Count - 1; i >= 0; i--)
            {
                var (index, waiter) = _entryWaiters[i];
                if (_writes.Count > index)
                {
                    waiter.TrySetResult();
                    _entryWaiters.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>
    /// An <see cref="IAgentRunner"/> whose <c>SetConfigProvisioner</c> runs a test callback — the
    /// FALLIBLE post-publication setup step. The interface permits an implementation to throw, and
    /// production calls it after the connection is published, so this is the seam that exercises the
    /// setup interval's cleanup coverage.
    /// <para>
    /// ONLY THE FIRST (INSTALL) CALL runs the callback and throws. The lifecycle calls this member a
    /// SECOND time to DETACH the run's callback at quiescence, and that call must be observable as a
    /// separate event rather than re-running the install step: a fixture that treated both calls
    /// alike would rewrite the captured publication observation during teardown. The detach call is
    /// still COUNTED and still throws (via <see cref="DetachFailure"/> when armed), so the test can
    /// additionally prove a throwing detachment neither replaces the setup failure nor skips the
    /// release of the run guard.
    /// </para>
    /// </summary>
    private sealed class ThrowingSetupRunner(Action onSetConfigProvisioner) : IAgentRunner
    {
        private int _calls;

        /// <summary>How many times <c>SetConfigProvisioner</c> was invoked (install and detach).</summary>
        internal int SetConfigProvisionerCalls => Volatile.Read(ref _calls);

        /// <summary>Whether a later call received <c>null</c> — i.e. the run genuinely detached.</summary>
        internal bool Detached { get; private set; }

        /// <summary>When armed, a DETACH call throws this instead of returning.</summary>
        internal Exception? DetachFailure { get; set; }

        public void SetConfigProvisioner(Func<string?, CancellationToken, Task>? provisioner)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                // THE INSTALL: the fallible post-publication setup step under test.
                onSetConfigProvisioner();
                return;
            }

            // THE DETACH (every later call). Recorded, and fallible when the test armed a failure.
            if (provisioner is null)
                Detached = true;

            if (DetachFailure is { } failure)
                throw failure;
        }

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ResetSessionAsync(string? model, ReasoningEffort? reasoningEffort, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
            => Task.FromResult(string.Empty);

        public TestResultReport? LastTestReport => null;
        public WorkerReport? LastWorkerReport => null;
        public void ClearTestReport() { }
        public void ClearWorkerReport() { }
        public void SetToolBridge(IToolCallBridge? bridge) { }
        public void SetCurrentTaskId(string? taskId) { }
        public void SetCurrentGoalId(string? goalId) { }
        public void SetTesterReport(string? report) { }
        public void SetCustomAgent(DomainWorkerRole role, string agentsMdContent) { }
        public void SetSession(object? session) { }
        public object? GetSession() => null;
        public void SetMaxContextTokens(int maxTokens) { }
        public int GetContextUsagePercent() => 0;
        public void SetCompactionModel(string? model) { }
        public void SetCompactionMaxTokens(int? maxTokens) { }
        public void SetSubAgentModels(IReadOnlyList<SubAgentModelDto> models) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// An <see cref="IAgentRunner"/> recording the SERVICE/RUNNER LIFETIME interactions the real
    /// <see cref="WorkerService"/> performs: <c>ConnectAsync</c> (preparation), every
    /// <c>SetConfigProvisioner</c> call IN ORDER, and disposal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ORDERED <c>ProvisionerHistory</c> is the observable that makes detachment provable: it
    /// records <c>"callback"</c> for an install and <c>null</c> for a detach, so a missing detach (or
    /// a detach ordered after a following install) is directly visible instead of being inferred.
    /// </para>
    /// <para>
    /// All observables are TCS/counters: the gates are test-controlled, and every await is bounded by
    /// the fixture's failsafe. Nothing here polls or sleeps.
    /// </para>
    /// </remarks>
    private sealed class LifetimeProbeRunner : IAgentRunner
    {
        private readonly object _gate = new();
        private readonly List<string?> _provisionerHistory = [];
        private int _connectCalls;
        private int _disposeCalls;
        private int _detachInvoked;

        /// <summary>Gate for <c>ConnectAsync</c>; <c>null</c> completes immediately.</summary>
        internal TaskCompletionSource<bool>? ConnectGate { get; set; }

        /// <summary>
        /// When non-null, <c>ConnectAsync</c> THROWS it after being counted/entered — a run whose
        /// preparation fails BEFORE any provisioning callback was ever installed.
        /// </summary>
        internal Exception? ConnectFailure { get; set; }

        /// <summary>Completed once <c>ConnectAsync</c> has been entered.</summary>
        internal TaskCompletionSource<bool> ConnectEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>When non-null, the next DETACH throws this instead of returning.</summary>
        internal Exception? DetachFailureOnce { get; set; }

        /// <summary>When non-null, disposal returns a ValueTask faulted with it.</summary>
        internal Exception? DisposeFailure { get; set; }

        /// <summary>Invoked for EVERY <c>SetConfigProvisioner</c> call, with the supplied callback.</summary>
        internal Action<Func<string?, CancellationToken, Task>?>? OnSetConfigProvisioner { get; set; }

        internal int ConnectCalls => Volatile.Read(ref _connectCalls);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

        /// <summary>How many times a DETACH was requested (a <c>null</c> provisioner was installed).</summary>
        internal int DetachInvoked => Volatile.Read(ref _detachInvoked);

        /// <summary>
        /// The ORDERED record of <c>SetConfigProvisioner</c> calls: <c>"callback"</c> for an install,
        /// <c>null</c> for a detach.
        /// </summary>
        internal IReadOnlyList<string?> ProvisionerHistory
        {
            get { lock (_gate) return [.. _provisionerHistory]; }
        }

        public void SetConfigProvisioner(Func<string?, CancellationToken, Task>? provisioner)
        {
            lock (_gate)
                _provisionerHistory.Add(provisioner is null ? null : "callback");

            OnSetConfigProvisioner?.Invoke(provisioner);

            if (provisioner is not null)
                return;

            Interlocked.Increment(ref _detachInvoked);

            if (DetachFailureOnce is { } failure)
            {
                DetachFailureOnce = null;
                throw failure;
            }
        }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _connectCalls);
            ConnectEntered.TrySetResult(true);

            if (ConnectFailure is { } prepareFailure)
                throw prepareFailure;

            if (ConnectGate is { } gate)
                await gate.Task.WaitAsync(ct);
        }

        public Task ResetSessionAsync(string? model, ReasoningEffort? reasoningEffort, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
            => Task.FromResult(string.Empty);

        public TestResultReport? LastTestReport => null;
        public WorkerReport? LastWorkerReport => null;
        public void ClearTestReport() { }
        public void ClearWorkerReport() { }
        public void SetToolBridge(IToolCallBridge? bridge) { }
        public void SetCurrentTaskId(string? taskId) { }
        public void SetCurrentGoalId(string? goalId) { }
        public void SetTesterReport(string? report) { }
        public void SetCustomAgent(DomainWorkerRole role, string agentsMdContent) { }
        public void SetSession(object? session) { }
        public object? GetSession() => null;
        public void SetMaxContextTokens(int maxTokens) { }
        public int GetContextUsagePercent() => 0;
        public void SetCompactionModel(string? model) { }
        public void SetCompactionMaxTokens(int? maxTokens) { }
        public void SetSubAgentModels(IReadOnlyList<SubAgentModelDto> models) { }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);

            return DisposeFailure is { } failure
                ? ValueTask.FromException(failure)
                : ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// An <see cref="IAgentRunner"/> that records the provisioning callback the lifecycle hands it,
    /// so a test can prove one instance backs both provisioning sites.
    /// </summary>
    private sealed class ProvisionerCapturingRunner : IAgentRunner
    {
        private readonly object _gate = new();

        /// <summary>
        /// APPEND-ONLY record of the REAL enclosing assignment executions
        /// (<c>ActiveAssignment.Execution</c>) this double observed, discovered through
        /// <see cref="ExecutionProbe"/> at the production boundaries it already sees.
        /// </summary>
        /// <remarks>
        /// A completed placeholder per prompt is NOT recorded here: it would assert that the
        /// assignment work is terminal while the enclosing <c>Task.Run</c> body is still creating
        /// its result and writing Complete/Ready, which is exactly the surrogate hazard teardown
        /// must avoid. Only the real execution is admitted.
        /// </remarks>
        private readonly List<Task> _assignmentExecutions = [];

        /// <summary>Set once <see cref="SealRecording"/> has run.</summary>
        private bool _sealed;

        /// <summary>Set whenever an execution is admitted AFTER the seal.</summary>
        private bool _recordedSinceSeal;

        internal Func<string?, CancellationToken, Task>? ConfigProvisioner { get; private set; }

        /// <summary>
        /// Snapshot of every REAL enclosing assignment execution this double ever observed. This is
        /// the authoritative teardown join set.
        /// </summary>
        internal IReadOnlyList<Task> AssignmentExecutions
        {
            get { lock (_gate) return [.. _assignmentExecutions]; }
        }

        /// <summary>
        /// Reads the service's CURRENT <c>ActiveAssignment.Execution</c>, or <c>null</c> when the
        /// ownership slot is empty. Installed by the fixture so this double records the real
        /// execution instead of a surrogate.
        /// </summary>
        internal Func<Task?>? ExecutionProbe { get; set; }

        /// <summary>Whether an execution was admitted AFTER <see cref="SealRecording"/>.</summary>
        internal bool RecordedSinceSeal
        {
            get { lock (_gate) return _recordedSinceSeal; }
        }

        /// <summary>
        /// CLOSURE HANDSHAKE — latches the recording set. Recording continues so late work stays
        /// visible; every later admission is flagged so teardown can re-drain to a fixpoint.
        /// </summary>
        internal IReadOnlyList<Task> SealRecording()
        {
            lock (_gate)
            {
                _sealed = true;
                _recordedSinceSeal = false;
                return [.. _assignmentExecutions];
            }
        }

        /// <summary>Clears the post-seal flag and returns the snapshot for the next drain pass.</summary>
        internal IReadOnlyList<Task> TakeRecordedSinceSeal()
        {
            lock (_gate)
            {
                _recordedSinceSeal = false;
                return [.. _assignmentExecutions];
            }
        }

        /// <summary>
        /// Records the REAL enclosing execution currently installed, if any. Idempotent by reference.
        /// </summary>
        private void RecordCurrentExecution()
        {
            if (ExecutionProbe?.Invoke() is not { } execution)
                return;

            lock (_gate)
            {
                foreach (var recorded in _assignmentExecutions)
                {
                    if (ReferenceEquals(recorded, execution))
                        return;
                }

                _assignmentExecutions.Add(execution);
                if (_sealed)
                    _recordedSinceSeal = true;
            }
        }

        public void SetConfigProvisioner(Func<string?, CancellationToken, Task>? provisioner) =>
            ConfigProvisioner = provisioner;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ResetSessionAsync(string? model, ReasoningEffort? reasoningEffort, CancellationToken ct = default)
        {
            // The reset for assignment N+1 runs while assignment N may still be installed.
            RecordCurrentExecution();
            return Task.CompletedTask;
        }

        public Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            // Record the REAL enclosing execution — never a completed placeholder standing in for
            // assignment work that is still running.
            RecordCurrentExecution();
            return Task.FromResult(string.Empty);
        }

        public TestResultReport? LastTestReport => null;
        public WorkerReport? LastWorkerReport => null;
        public void ClearTestReport() { }
        public void ClearWorkerReport() { }
        public void SetToolBridge(IToolCallBridge? bridge) { }
        public void SetCurrentTaskId(string? taskId) { }
        public void SetCurrentGoalId(string? goalId) { }
        public void SetTesterReport(string? report) { }
        public void SetCustomAgent(DomainWorkerRole role, string agentsMdContent) { }
        public void SetSession(object? session) { }
        public object? GetSession() => null;
        public void SetMaxContextTokens(int maxTokens) { }
        public int GetContextUsagePercent() => 0;
        public void SetCompactionModel(string? model) { }
        public void SetCompactionMaxTokens(int? maxTokens) { }
        public void SetSubAgentModels(IReadOnlyList<SubAgentModelDto> models) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
