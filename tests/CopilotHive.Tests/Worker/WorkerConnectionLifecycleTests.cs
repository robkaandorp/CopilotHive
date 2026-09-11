using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;
using CopilotHive.Workers;

using Grpc.Core;

using Microsoft.Extensions.AI;

using System.Reflection;
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

        using var service = BuildService(runner, provisionerHarness.Provisioner, configRepoDir);

        var requests = new RecordingRequestStream();
        var responses = new ChannelResponseReader();

        // Hoisted so the stream's disposal callback can observe the ACTUAL connection object (it is
        // unpublished by then, so reading the field would only ever see null).
        WorkerConnection? connection = null;

        // Observed INSIDE each write: whether a connection was already published and still usable AT
        // the moment that write was issued. This makes publish-BEFORE-Ready a real ordering claim
        // rather than a post-hoc read.
        var publishedAtEachWrite = new List<bool>();
        requests.OnWrite = _ =>
            publishedAtEachWrite.Add(GetPublishedConnection(service) is { IsRetired: false });

        // The stream's disposal callback runs DURING disposal, so it observes the teardown ordering.
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

        service.CallInvokerFactory = () => invoker;
        service.WorkStreamFactory = (_, _) => stream;

        using var loopCts = new CancellationTokenSource();
        var run = Task.CompletedTask;

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

            // The LAZY runner callback reaches that instance and performs one fetch on this
            // fixture's in-memory environment.
            Assert.Equal(0, provisionerHarness.FetchCount);
            await runner.ConfigProvisioner!(FixtureModel, TestContext.Current.CancellationToken);
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
            var overrideFetchesBefore = provisionerHarness.FetchCount;
            var fetchCallsBefore = invoker.WorkerConfigCalls;
            var lazyFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => runner.ConfigProvisioner!(FixtureModel, TestContext.Current.CancellationToken));
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
            await loopCts.CancelAsync();
            responses.TryComplete();
            await ObserveForTeardownAsync(run);
            TryDelete(configRepoDir);
        }
    }

    /// <summary>
    /// A REJECTED registration publishes NOTHING usable: no connection is observable afterwards, no
    /// stream was ever opened, the runner was never handed a provisioner callback, and every access
    /// path still fails with the EXISTING disconnected error.
    /// </summary>
    [Fact]
    public async Task RunAsync_RejectedRegistration_PublishesNothingAndOpensNoStream()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse { Accepted = false });
        var runner = new ProvisionerCapturingRunner();
        var streamOpened = 0;
        using var service = BuildService(runner, new ProvisionerHarness().Provisioner);
        service.CallInvokerFactory = () => invoker;
        service.WorkStreamFactory = (_, _) =>
        {
            Interlocked.Increment(ref streamOpened);
            throw new InvalidOperationException("No stream may be opened for a rejected registration.");
        };

        await service.RunAsync(TestContext.Current.CancellationToken);

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
        using var service = BuildService(runner, witness);

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
        var run = Task.CompletedTask;

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
            await loopCts.CancelAsync();
            responses.TryComplete();
            await ObserveForTeardownAsync(run);
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

        using var service = BuildService(runner, new ProvisionerHarness().Provisioner);
        serviceRef = service;

        var stream = new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
            new RecordingRequestStream(),
            new ChannelResponseReader(),
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

        // The setup step throws, so the failure propagates to the caller unchanged.
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RunAsync(TestContext.Current.CancellationToken)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken));
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

        // And the service is genuinely disconnected afterwards — no nominally usable connection.
        var sessionFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetSessionAsync("goal:role", TestContext.Current.CancellationToken));
        Assert.Equal(WorkerConnection.DisconnectedMessage, sessionFailure.Message);
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

        using var service = BuildService(new ProvisionerCapturingRunner(), witness);

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
            await ObserveForTeardownAsync(loop);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // (2) Focused fake-client connection tests.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// SESSION LOAD and SAVE go through the CONNECTION's own client, carrying the exact arguments
    /// the caller supplied, and a not-found response loads as <c>null</c>.
    /// </summary>
    [Fact]
    public async Task SessionLoadAndSave_UseTheConnectionClient_WithExactArguments()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse { Accepted = true });
        invoker.SessionToReturn = new GetSessionResponse { Found = true, SessionJson = "{\"turn\":7}" };
        using var service = BuildService(new ProvisionerCapturingRunner(), new ProvisionerHarness().Provisioner);
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

        using var service = BuildService(new ProvisionerCapturingRunner(), new ProvisionerHarness().Provisioner);
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

    /// <summary>
    /// The connection's PRODUCTION provisioning path is retirement-gated: a fetch after retirement
    /// fails with the EXISTING disconnected error and starts NO transport, while a fetch that
    /// already passed the check keeps its captured client and outcome.
    /// </summary>
    [Fact]
    public async Task ProductionProvisioningPath_FailsDisconnectedAfterRetirement_WithoutTransport()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse { Accepted = true });
        using var service = BuildService(new ProvisionerCapturingRunner(), new ProvisionerHarness().Provisioner);
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
        using var service = BuildService(new ProvisionerCapturingRunner(), new ProvisionerHarness().Provisioner);
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

    /// <summary>
    /// HEARTBEAT ARGUMENT FORWARDING, driven through the factored ONE-TICK sender — no 30-second
    /// tick is awaited anywhere. The tick snapshots the connection's identity and the worker's
    /// current task state, and a RETIRED connection issues no heartbeat at all.
    /// </summary>
    [Fact]
    public async Task HeartbeatTick_ForwardsConnectionIdentityAndTaskState_AndSkipsWhenRetired()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse { Accepted = true });
        using var service = BuildService(new ProvisionerCapturingRunner(), new ProvisionerHarness().Provisioner);
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
        using var service = new WorkerService("http://localhost:9999", LocalWorkerId, ["coder"]);
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
            await JoinForTeardownAsync(holder);
            await JoinForTeardownAsync(queued);
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

        using var service = new WorkerService("http://localhost:9999", SharedWorkerId, ["coder"]);

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
            await JoinForTeardownAsync(holder);
            await JoinForTeardownAsync(contender);
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

    private static WorkerService BuildService(
        IAgentRunner runner, WorkerConfigProvisioner provisioner, string configRepoDir = "/config-repo")
    {
        var service = new WorkerService("http://localhost:9999", LocalWorkerId, ["coder"], configRepoDir);
        service.TestProvisioner = provisioner;
        ReplaceRunner(service, runner);
        return service;
    }

    private static void ReplaceRunner(WorkerService service, IAgentRunner runner)
    {
        var field = typeof(WorkerService).GetField("_agentRunner", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("WorkerService._agentRunner field not found.");
        if (field.GetValue(service) is IAgentRunner existing)
            existing.DisposeAsync().AsTask().GetAwaiter().GetResult();
        field.SetValue(service, runner);
    }

    /// <summary>Reflects the real published-connection field (observation only).</summary>
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

    /// <summary>Bounded failsafe join that never masks an assertion failure.</summary>
    private static async Task ObserveForTeardownAsync(Task? producer)
    {
        if (producer is null) return;
        try
        {
            await producer.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        catch (Exception)
        {
            // Teardown only: the loop may fault (cancelled teardown, reader fault). Its real
            // outcome was asserted in the try body; rethrowing here would mask that assertion.
        }
    }

    private static Task JoinForTeardownAsync(Task? producer) => ObserveForTeardownAsync(producer);

    // ── Fakes ─────────────────────────────────────────────────────────────────

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
            return Task.CompletedTask;
        }

        public Task CompleteAsync() => Task.CompletedTask;

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
    /// </summary>
    private sealed class ThrowingSetupRunner(Action onSetConfigProvisioner) : IAgentRunner
    {
        public void SetConfigProvisioner(Func<string?, CancellationToken, Task>? provisioner) =>
            onSetConfigProvisioner();

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
    /// An <see cref="IAgentRunner"/> that records the provisioning callback the lifecycle hands it,
    /// so a test can prove one instance backs both provisioning sites.
    /// </summary>
    private sealed class ProvisionerCapturingRunner : IAgentRunner
    {
        internal Func<string?, CancellationToken, Task>? ConfigProvisioner { get; private set; }

        public void SetConfigProvisioner(Func<string?, CancellationToken, Task>? provisioner) =>
            ConfigProvisioner = provisioner;

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
}
