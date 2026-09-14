using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;
using CopilotHive.Workers;

using Grpc.Core;

using Microsoft.Extensions.AI;

using System.Reflection;

using DomainWorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// THE PRODUCTION-CHAIN A/B LIFECYCLE: two FRESH <see cref="WorkerService"/> instances built through
/// the SAME internal attempt-construction path <c>Program.cs</c> uses, SHARING one
/// <see cref="WorkerProvisioningEnvironment"/>, each running the REAL
/// <see cref="WorkerService.RunAsync"/> over the fake call-invoker / duplex-stream seams with the
/// PRODUCTION-CREATED provisioner (never <c>TestProvisioner</c>).
/// <para>
/// <b>What it proves.</b> Attempt A registers (<c>worker-assigned-a</c>), provisions server-supplied
/// configuration into the shared fake environment through its own production provisioner, then loses
/// its transport: the loop drains, retires and unpublishes the connection, and the captured LAZY
/// runner callback a runner cached for A must afterwards start NO fetch — it fails with the EXISTING
/// disconnected error. Attempt B is then built through the SAME constructor path with the SAME state
/// object, registers under its OWN accepted identity, and provisions. Because the snapshot belongs to
/// the PROCESS (taken before A ever provisioned), B's response REPLACES the setting it carries and
/// CLEARS the A-owned values it omits instead of promoting them to operator overrides — while the
/// GENUINE original operator values survive both attempts.
/// </para>
/// <para>
/// Only environment provenance is shared: the two attempts keep separate clients, streams,
/// provisioners and response provenance, which the identity and reference assertions below pin.
/// </para>
/// <para>
/// <b>SEQUENTIAL QUIESCENCE IS PROVEN, NOT ASSUMED.</b> The production contract is for sequential
/// QUIESCENT attempts, so attempt A is fully wound down BEFORE attempt B is constructed: A's loop
/// token is cancelled, A's reader is completed, A's <c>RunAsync</c> handle and every wait its reader
/// ever created are DRAINED within the failure bound, and A's service is DISPOSED — all inside A's own
/// scope. A dedicated block of assertions then pins that state (no in-flight read, no pending waiter,
/// a completed run handle, a disposed service, an empty teardown ledger) before B exists at all.
/// </para>
/// <para>
/// <b>NO ABANDONED TASK EXISTS.</b> <see cref="ScriptedReader"/> deliberately avoids
/// <see cref="Task.WhenAny(Task[])"/>: a losing branch there would leave a real pending task behind
/// with nothing to await it. Instead every <c>MoveNext</c> awaits exactly ONE waiter that the fault,
/// the EOF and the cancellation registration all settle, so the task a read creates is always the task
/// that read observes.
/// </para>
/// <para>
/// <b>TEARDOWN ENFORCES COMPLETION.</b> <see cref="TeardownLedger"/> never swallows a failure to
/// drain: a handle still running when the bound expires is RECORDED and the recorded failures are
/// ASSERTED, so an abandoned producer fails this test by name instead of disappearing into a
/// catch-all. The bound is a failure bound only — it orders nothing.
/// </para>
/// <para>
/// <b>Removal demonstration.</b> Building B's service with FRESH provenance over the same fake
/// environment (the stale-operator regression) makes B's snapshot capture A's provisioned values, so
/// the "A-owned value is CLEARED" assertion observes A's value and fails.
/// </para>
/// <para>
/// No real credentials, no process-environment mutation and no network: the environment is a fake
/// in-memory dictionary and every RPC is answered by a fake invoker. Every gate is a
/// <see cref="TaskCompletionSource"/>, a counted write, or a channel fault/EOF — there are NO sleeps
/// and NO <c>Task.Delay</c> ordering anywhere in this fixture.
/// </para>
/// </summary>
[Collection("ConsoleOutput")]
public sealed class WorkerProvisioningEnvironmentProductionChainTests
{
    private const string LocalWorkerId = "worker-local";
    private const string AssignedIdA = "worker-assigned-a";
    private const string AssignedIdB = "worker-assigned-b";
    private const string FixtureModel = "copilot/fixture-model";

    /// <summary>
    /// The FAILURE BOUND for every drain and every gate. It is never an ordering device: nothing waits
    /// for it to elapse, and its expiry always FAILS the test (either directly, through
    /// <see cref="Task.WaitAsync(TimeSpan, CancellationToken)"/>'s <see cref="TimeoutException"/>, or
    /// through a recorded — and asserted — <see cref="TeardownLedger"/> failure).
    /// </summary>
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(30);

    private const string OperatorOllamaUrl = "http://operator:11434";
    private const string OperatorGithubToken = "operator-original-github-token";
    private const string OperatorConfigRepoUrl = "https://github.com/operator/repo.git";

    /// <summary>
    /// TWO SEQUENTIAL ATTEMPTS, ONE SHARED PROVENANCE — the whole contract in one flow, with attempt A
    /// provably quiescent and disposed before attempt B is built.
    /// </summary>
    [Fact]
    public async Task TwoSequentialAttempts_SharedProvenance_ReplaceAndClearUnderEachAttemptsIdentity()
    {
        // Every drain in this test reports into ONE ledger, which is asserted empty at the end (and
        // again between the two attempts), so no failure to converge can be silently swallowed.
        var teardown = new TeardownLedger();

        // The FAKE process environment plus the ONE provenance object Program.cs would create
        // outside its retry loop. Nothing here touches the real process environment.
        var env = new FakeEnv(
            (WorkerConfigProvisioner.OllamaUrlVar, OperatorOllamaUrl),
            (WorkerConfigProvisioner.GitHubTokenVar, OperatorGithubToken),
            (WorkerConfigProvisioner.ConfigRepoUrlVar, OperatorConfigRepoUrl));
        var shared = new WorkerProvisioningEnvironment(env.Read, env.Write);

        // ══ Attempt A ═════════════════════════════════════════════════════════════
        var invokerA = new FakeInvoker(new RegisterResponse
        {
            Accepted = true,
            AssignedWorkerId = AssignedIdA,
        });
        invokerA.WorkerConfigToReturn = new GetWorkerConfigResponse
        {
            GithubToken = "ghp_attempt_a",
            LlmProvider = "ollama-cloud",
            OllamaModel = "attempt-a-model",
            OllamaApiKey = "attempt-a-key",
            ConfigRepoUrl = "https://github.com/org/attempt-a.git",
        };

        var runnerA = new CapturingRunner();
        var readerA = new ScriptedReader();
        var writerA = new RecordingWriter();

        // The observation is taken INSIDE the stream's disposal callback, so it records the state AT
        // the moment the transport is torn down. The connection is hoisted because by disposal time it
        // is already UNPUBLISHED, so reading the service field would only ever see null.
        WorkerConnection? connectionA = null;
        WorkerService? attemptA = null;
        var retiredAtStreamDisposalA = false;
        var unpublishedAtStreamDisposalA = false;
        var streamDisposalsA = 0;

        attemptA = BuildAttempt(shared, runnerA, invokerA, readerA, writerA, onStreamDisposed: () =>
        {
            Interlocked.Increment(ref streamDisposalsA);
            retiredAtStreamDisposalA = connectionA?.IsRetired ?? false;
            unpublishedAtStreamDisposalA = GetPublishedConnection(attemptA!) is null;
        });

        // Deliberately NOT `using`: A's disposal must happen inside A's own scope, BEFORE B is built —
        // a loop-scoped `using` would defer it past the whole of attempt B.
        var serviceA = attemptA;
        var loopCtsA = new CancellationTokenSource();
        var runA = Task.CompletedTask;
        var serviceADisposed = false;

        // Needed by attempt B's "A started no further fetch" assertion.
        var fetchCallsAfterRetiredCallback = 0;

        try
        {
            runA = serviceA.RunAsync(loopCtsA.Token);

            // BARRIER: the initial Ready is written strictly AFTER publication.
            await writerA.WaitForWriteCountAsync(1, TestContext.Current.CancellationToken);
            Assert.Equal(WorkerMessage.PayloadOneofCase.Ready, writerA.Writes[0].PayloadCase);
            Assert.Equal(AssignedIdA, writerA.Writes[0].WorkerId);

            connectionA = Assert.IsType<WorkerConnection>(GetPublishedConnection(serviceA));

            // The PRODUCTION provisioner is in place: the TestProvisioner seam is untouched, and the
            // connection built its own provisioner (with its own checked fetch).
            Assert.Null(serviceA.TestProvisioner);
            Assert.NotNull(connectionA.Provisioner);

            // The LAZY runner callback is the connection-owned wrapper, and reaching it provisions
            // through A's OWN production provisioner and A's OWN client identity.
            Assert.NotNull(runnerA.ConfigProvisioner);
            Assert.Equal(0, invokerA.WorkerConfigCalls);
            await runnerA.ConfigProvisioner!(FixtureModel, TestContext.Current.CancellationToken);
            Assert.Equal(1, invokerA.WorkerConfigCalls);
            Assert.Equal(AssignedIdA, invokerA.LastWorkerConfigWorkerId);

            // A's provisioned values are in the shared fake environment; the operator alias
            // suppressed the GH_TOKEN mirror, and the operator's OLLAMA_URL was never overwritten.
            Assert.Equal("ollama-cloud", env[WorkerConfigProvisioner.LlmProviderVar]);
            Assert.Equal("attempt-a-model", env[WorkerConfigProvisioner.OllamaModelVar]);
            Assert.Equal("attempt-a-key", env[WorkerConfigProvisioner.OllamaApiKeyVar]);
            Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
            Assert.Equal(OperatorOllamaUrl, env[WorkerConfigProvisioner.OllamaUrlVar]);
            Assert.Equal(OperatorGithubToken, env[WorkerConfigProvisioner.GitHubTokenVar]);
            Assert.Equal("ghp_attempt_a", connectionA.Provisioner.ResolveConfigRepoCredential());
            Assert.Equal("https://github.com/org/attempt-a.git", connectionA.Provisioner.ProvisionedConfigRepoUrl);

            // A's TRANSPORT FAILS: the stream surfaces an availability fault, so the real loop drains
            // and retires the connection and RunAsync faults with the retryable RpcException category.
            readerA.Fail(new RpcException(new Status(StatusCode.Unavailable, "transport lost")));

            var transportFailure = await Assert.ThrowsAsync<RpcException>(
                () => runA.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Equal(StatusCode.Unavailable, transportFailure.StatusCode);

            // Retirement and unpublication completed, and both happened BEFORE the transport disposal.
            Assert.True(connectionA.IsRetired);
            Assert.Null(GetPublishedConnection(serviceA));
            Assert.True(retiredAtStreamDisposalA, "A's connection must be retired before its stream is disposed.");
            Assert.True(unpublishedAtStreamDisposalA, "A's connection must be unpublished before its stream is disposed.");
            Assert.Equal(1, streamDisposalsA);

            // A'S CAPTURED RETIRED CALLBACK STARTS NO FETCH. Retirement is checked before the fetch
            // delegate, so the override-free production path fails with the EXISTING disconnected
            // error and A's client sees no further RPC.
            var fetchCallsBeforeRetiredCallback = invokerA.WorkerConfigCalls;
            var retiredCallbackFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => runnerA.ConfigProvisioner!(FixtureModel, TestContext.Current.CancellationToken));
            Assert.Equal(WorkerConnection.DisconnectedMessage, retiredCallbackFailure.Message);
            Assert.Equal(fetchCallsBeforeRetiredCallback, invokerA.WorkerConfigCalls);
            fetchCallsAfterRetiredCallback = invokerA.WorkerConfigCalls;
        }
        finally
        {
            // ── A-SCOPE QUIESCENCE, BEFORE ATTEMPT B EXISTS ──────────────────────
            // Cancel A's loop token, settle A's reader, then DRAIN both the RunAsync handle and every
            // wait A's reader ever created. Each drain is bounded, and a handle still running at the
            // bound is RECORDED as a teardown failure rather than abandoned.
            await loopCtsA.CancelAsync();
            readerA.Complete();

            await teardown.DrainAsync("attempt A RunAsync", runA);
            await teardown.DrainAsync("attempt A reader waits", readerA.WhenAllWaitsSettledAsync());

            // A's service is disposed HERE — inside A's own scope, so its disposal provably completes
            // before attempt B is constructed.
            serviceA.Dispose();
            serviceADisposed = true;
            loopCtsA.Dispose();
        }

        // ── QUIESCENCE PROOF: attempt A is fully wound down before B is built ─────
        // Reached only when the A body succeeded (a failing body propagates out of the finally above),
        // which is exactly when this proof must hold.
        Assert.True(runA.IsCompleted, "Attempt A's RunAsync must be completed before attempt B starts.");
        Assert.Equal(0, readerA.InFlightReads);
        Assert.Equal(0, readerA.PendingWaiterCount);
        Assert.True(readerA.WhenAllWaitsSettledAsync().IsCompleted,
            "Every wait attempt A's reader created must be settled before attempt B starts.");
        Assert.True(serviceADisposed, "Attempt A's service must be disposed before attempt B is constructed.");
        AssertTeardownDrained(teardown);

        // ══ Attempt B: a FRESH service through the SAME attempt-construction path ══
        var invokerB = new FakeInvoker(new RegisterResponse
        {
            Accepted = true,
            AssignedWorkerId = AssignedIdB,
        });
        invokerB.WorkerConfigToReturn = new GetWorkerConfigResponse
        {
            LlmProvider = "copilot",
        };

        var runnerB = new CapturingRunner();
        var readerB = new ScriptedReader();
        var writerB = new RecordingWriter();

        // THE SAME state object that A used: this is what Program.cs does on every retry.
        var serviceB = BuildAttempt(shared, runnerB, invokerB, readerB, writerB);
        var loopCtsB = new CancellationTokenSource();
        var runB = Task.CompletedTask;
        var serviceBDisposed = false;

        try
        {
            runB = serviceB.RunAsync(loopCtsB.Token);

            await writerB.WaitForWriteCountAsync(1, TestContext.Current.CancellationToken);
            Assert.Equal(AssignedIdB, writerB.Writes[0].WorkerId);

            var connectionB = Assert.IsType<WorkerConnection>(GetPublishedConnection(serviceB));
            Assert.Null(serviceB.TestProvisioner);
            Assert.NotNull(connectionB.Provisioner);

            // Provenance is the ONLY thing shared: B has its OWN provisioner object, and neither
            // attempt is A's.
            Assert.NotSame(connectionA!.Provisioner, connectionB.Provisioner);

            Assert.NotNull(runnerB.ConfigProvisioner);
            await runnerB.ConfigProvisioner!(FixtureModel, TestContext.Current.CancellationToken);

            // B FETCHED THROUGH B: its own client and its own accepted identity.
            Assert.Equal(1, invokerB.WorkerConfigCalls);
            Assert.Equal(AssignedIdB, invokerB.LastWorkerConfigWorkerId);
            Assert.Equal(fetchCallsAfterRetiredCallback, invokerA.WorkerConfigCalls);

            // REPLACED — B's value, not A's.
            Assert.Equal("copilot", env[WorkerConfigProvisioner.LlmProviderVar]);
            // CLEARED — values only ever PROVISIONED (by A) are removed, NOT promoted to
            // operator overrides. With fresh provenance for B these would survive as A's values.
            Assert.Null(env[WorkerConfigProvisioner.OllamaModelVar]);
            Assert.Null(env[WorkerConfigProvisioner.OllamaApiKeyVar]);
            Assert.False(env.IsSet(WorkerConfigProvisioner.OllamaModelVar));
            Assert.False(env.IsSet(WorkerConfigProvisioner.OllamaApiKeyVar));

            // The GENUINE original operator values survive both attempts untouched.
            Assert.Equal(OperatorOllamaUrl, env[WorkerConfigProvisioner.OllamaUrlVar]);
            Assert.Equal(OperatorGithubToken, env[WorkerConfigProvisioner.GitHubTokenVar]);

            // B's RESPONSE provenance is its own: no URL or token came from A, so the chain falls
            // through to the ORIGINAL operator environment — the intentional documented fallback.
            Assert.Null(connectionB.Provisioner.ProvisionedConfigRepoUrl);
            Assert.Equal(OperatorConfigRepoUrl, connectionB.Provisioner.ResolvedConfigRepoUrl);
            Assert.Equal(OperatorGithubToken, connectionB.Provisioner.ResolveConfigRepoCredential());

            // A's PROVISIONED URL never reached the environment (only the operator value lives
            // there) and A's response provenance stays A's own.
            Assert.Equal(OperatorConfigRepoUrl, env[WorkerConfigProvisioner.ConfigRepoUrlVar]);
            Assert.Equal("https://github.com/org/attempt-a.git", connectionA.Provisioner!.ProvisionedConfigRepoUrl);

            // A's retirement is unaffected by B's attempt: it stays retired with its stale
            // in-memory response provenance, which no later attempt may inherit.
            Assert.True(connectionA.IsRetired);
            Assert.Equal("ghp_attempt_a", connectionA.Provisioner.ResolveConfigRepoCredential());

            // EOF ends B's loop cleanly, preserving the existing clean-return behavior.
            readerB.Complete();
            await runB.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(connectionB.IsRetired);
            Assert.Null(GetPublishedConnection(serviceB));
        }
        finally
        {
            // ── B-SCOPE QUIESCENCE, mirroring attempt A's ────────────────────────
            await loopCtsB.CancelAsync();
            readerB.Complete();

            await teardown.DrainAsync("attempt B RunAsync", runB);
            await teardown.DrainAsync("attempt B reader waits", readerB.WhenAllWaitsSettledAsync());

            serviceB.Dispose();
            serviceBDisposed = true;
            loopCtsB.Dispose();
        }

        // ── QUIESCENCE PROOF for attempt B, and the ENFORCED teardown verdict ─────
        Assert.True(runB.IsCompleted, "Attempt B's RunAsync must be completed at the end of the test.");
        Assert.Equal(0, readerB.InFlightReads);
        Assert.Equal(0, readerB.PendingWaiterCount);
        Assert.True(serviceBDisposed, "Attempt B's service must be disposed at the end of the test.");

        // THE ENFORCEMENT: any handle that failed to drain within the bound was recorded, and a
        // recorded failure fails this test by name instead of being swallowed by a catch-all.
        AssertTeardownDrained(teardown);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Harness.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds ONE attempt's service through the INTERNAL attempt-construction path <c>Program.cs</c>
    /// uses — the shared-provenance constructor — with the fake invoker and duplex stream installed.
    /// The runner is injected through the same reflection seam the existing WorkerService fixtures use.
    /// </summary>
    private static WorkerService BuildAttempt(
        WorkerProvisioningEnvironment shared,
        IAgentRunner runner,
        FakeInvoker invoker,
        ScriptedReader reader,
        RecordingWriter writer,
        Action? onStreamDisposed = null)
    {
        // The EXACT constructor path Program.cs takes: a fresh service per attempt, carrying the
        // process's ONE shared environment provenance.
        var service = new WorkerService(
            "http://localhost:9999",
            LocalWorkerId,
            ["coder"],
            shared);

        ReplaceRunner(service, runner);

        var stream = new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
            writer, reader,
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ => onStreamDisposed?.Invoke(),
            null!);

        service.CallInvokerFactory = () => invoker;
        service.WorkStreamFactory = (_, _) => stream;
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

    /// <summary>
    /// THE TEARDOWN VERDICT: a handle that never drained is a TEST FAILURE, reported by name.
    /// </summary>
    private static void AssertTeardownDrained(TeardownLedger ledger)
    {
        var failures = ledger.Failures;
        Assert.True(
            failures.Count == 0,
            "Teardown did not converge — attempts were not sequentially quiescent: "
                + string.Join(" | ", failures));
    }

    /// <summary>
    /// THE ENFORCING TEARDOWN DRAINER. It records — never swallows — a handle that fails to reach a
    /// terminal state within the failure bound, and the test ASSERTS the record is empty.
    /// </summary>
    /// <remarks>
    /// The distinction that matters: a handle that FAULTED or was CANCELLED is drained (an attempt's
    /// real outcome is asserted in the test body, so rethrowing it here would mask that assertion),
    /// whereas a handle that is STILL RUNNING after the bound was abandoned. <c>IsCompleted</c>
    /// decides between the two unambiguously, so a bound expiry can never be mistaken for a terminal
    /// outcome — which is exactly the failure mode a bare catch-all hides.
    /// </remarks>
    private sealed class TeardownLedger
    {
        private readonly object _gate = new();
        private readonly List<string> _failures = [];

        /// <summary>A snapshot of the recorded teardown failures.</summary>
        internal IReadOnlyList<string> Failures
        {
            get { lock (_gate) return [.. _failures]; }
        }

        /// <summary>
        /// Drains ONE handle within the failure bound, recording a failure when it is still running
        /// afterwards.
        /// </summary>
        /// <param name="what">The handle's name, used verbatim in the recorded failure.</param>
        /// <param name="handle">The handle to drain; <c>null</c> means there was nothing started.</param>
        internal async Task DrainAsync(string what, Task? handle)
        {
            if (handle is null) return;

            try
            {
                await handle.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            }
            catch (Exception)
            {
                // Terminal-outcome exceptions are expected here (a transport fault, a cancelled
                // teardown) and are asserted in the body. The IsCompleted check below is what decides
                // whether this handle actually drained — a bound expiry leaves it incomplete.
            }

            if (!handle.IsCompleted)
            {
                lock (_gate)
                {
                    _failures.Add(
                        $"'{what}' did not drain within {Failsafe.TotalSeconds:0}s — it is STILL RUNNING, "
                            + "so this attempt was never quiescent.");
                }
            }
        }
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    /// <summary>
    /// An in-memory process environment backed by an <c>Ordinal</c> dictionary, so no test mutates
    /// the real process environment and a REMOVED variable stays distinguishable from a never-set one.
    /// </summary>
    private sealed class FakeEnv
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

        internal FakeEnv(params (string Key, string? Value)[] initial)
        {
            foreach (var (key, value) in initial)
                _values[key] = value;
        }

        internal string? this[string name] => _values.TryGetValue(name, out var value) ? value : null;

        internal bool IsSet(string name) => _values.ContainsKey(name);

        internal string? Read(string name) =>
            _values.TryGetValue(name, out var value) ? value : null;

        internal void Write(string name, string? value)
        {
            if (value is null) _values.Remove(name);
            else _values[name] = value;
        }
    }

    /// <summary>
    /// A <see cref="CallInvoker"/> answering the unary RPCs the REAL <see cref="WorkerService.RunAsync"/>
    /// reaches — <c>Register</c>, <c>GetWorkerConfig</c>, <c>GetSession</c>, <c>SaveSession</c> and
    /// <c>Heartbeat</c> — recording the provisioning requests so a test can prove WHICH attempt's
    /// identity and client performed a fetch. Any other call is a fixture bug and throws loudly.
    /// </summary>
    private sealed class FakeInvoker(RegisterResponse registerResponse) : CallInvoker
    {
        private int _registerCalls;
        private int _workerConfigCalls;
        private string? _lastWorkerConfigWorkerId;
        private string? _lastRegisterWorkerId;

        internal GetWorkerConfigResponse WorkerConfigToReturn { get; set; } = new();

        internal int RegisterCalls => Volatile.Read(ref _registerCalls);
        internal int WorkerConfigCalls => Volatile.Read(ref _workerConfigCalls);
        internal string? LastWorkerConfigWorkerId => Volatile.Read(ref _lastWorkerConfigWorkerId);
        internal string? LastRegisterWorkerId => Volatile.Read(ref _lastRegisterWorkerId);

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
                "/copilothive.HiveOrchestrator/GetSession" => new GetSessionResponse { Found = false },
                "/copilothive.HiveOrchestrator/SaveSession" => new SaveSessionResponse { Success = true },
                "/copilothive.HiveOrchestrator/Heartbeat" => new HeartbeatResponse { Acknowledged = true },
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
    /// The orchestrator-message reader for ONE attempt. It ends the production loop exactly two ways,
    /// both deterministic signals and neither a delay: an availability FAULT (<see cref="Fail"/> — the
    /// transport failure under test, rethrown VERBATIM so the production loop observes the real
    /// <see cref="RpcException"/> category) or a clean EOF (<see cref="Complete"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>NO ABANDONED TASK.</b> An earlier version raced the fault signal against the channel read
    /// with <see cref="Task.WhenAny(Task[])"/>, which left the LOSING read pending with nothing to
    /// await it — so the attempt could not be proven quiescent. Here each <c>MoveNext</c> creates and
    /// awaits exactly ONE waiter, and the fault, the EOF and the cancellation registration all settle
    /// THAT waiter. The task a read creates is therefore always the task that read observes.
    /// </para>
    /// <para>
    /// <see cref="InFlightReads"/>, <see cref="PendingWaiterCount"/> and
    /// <see cref="WhenAllWaitsSettledAsync"/> make quiescence OBSERVABLE, so the test can assert that
    /// an attempt left nothing running instead of assuming it.
    /// </para>
    /// </remarks>
    private sealed class ScriptedReader : IAsyncStreamReader<OrchestratorMessage>
    {
        private readonly object _gate = new();

        /// <summary>The waiters no read has observed yet — zero once every read has unwound.</summary>
        private readonly List<TaskCompletionSource<bool>> _pendingWaiters = [];

        /// <summary>EVERY wait ever created, so teardown can prove they all settled.</summary>
        private readonly List<Task> _allWaits = [];

        private Exception? _failure;
        private bool _completed;
        private int _inFlightReads;

        public OrchestratorMessage Current { get; private set; } = null!;

        /// <summary>Reads that have ENTERED <c>MoveNext</c> and not yet returned.</summary>
        internal int InFlightReads => Volatile.Read(ref _inFlightReads);

        /// <summary>Waits that have not been observed by their own read yet.</summary>
        internal int PendingWaiterCount
        {
            get { lock (_gate) return _pendingWaiters.Count; }
        }

        /// <summary>
        /// Signals a transport fault. The waiting (or next) <c>MoveNext</c> throws it VERBATIM, so the
        /// production loop sees the real exception type rather than a wrapper.
        /// </summary>
        internal void Fail(Exception exception)
        {
            lock (_gate)
                _failure ??= exception;

            ReleaseWaiters();
        }

        /// <summary>Ends the stream cleanly (EOF). Idempotent.</summary>
        internal void Complete()
        {
            lock (_gate)
                _completed = true;

            ReleaseWaiters();
        }

        /// <summary>
        /// A handle completing once EVERY wait this reader ever created has settled. It never throws —
        /// each wait is observed — so teardown can bound it and record a genuine failure to drain
        /// rather than swallowing one.
        /// </summary>
        internal Task WhenAllWaitsSettledAsync()
        {
            List<Task> snapshot;
            lock (_gate)
                snapshot = [.. _allWaits];

            return Task.WhenAll(snapshot.Select(Observed));

            static Task Observed(Task wait) =>
                wait.ContinueWith(
                    static completed => { _ = completed.Exception; },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _inFlightReads);
            try
            {
                while (true)
                {
                    TaskCompletionSource<bool> waiter;
                    lock (_gate)
                    {
                        // A fault always wins, then EOF: both are terminal and checked before any wait
                        // is created, so a signal that arrives first is never missed.
                        if (_failure is { } failure)
                            throw failure;

                        if (_completed)
                            return false;

                        waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _pendingWaiters.Add(waiter);
                        _allWaits.Add(waiter.Task);
                    }

                    // The cancellation registration settles THE SAME waiter, so a cancelled read
                    // unwinds with OperationCanceledException without leaving any other task pending.
                    await using var registration = cancellationToken.Register(
                        static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(), waiter);

                    try
                    {
                        await waiter.Task;
                    }
                    finally
                    {
                        lock (_gate)
                            _pendingWaiters.Remove(waiter);
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inFlightReads);
            }
        }

        /// <summary>Settles every outstanding waiter so its own read can re-evaluate the state.</summary>
        private void ReleaseWaiters()
        {
            List<TaskCompletionSource<bool>> snapshot;
            lock (_gate)
                snapshot = [.. _pendingWaiters];

            foreach (var waiter in snapshot)
                waiter.TrySetResult(true);
        }
    }

    /// <summary>
    /// Records every <see cref="WorkerMessage"/> an attempt writes and lets a test await a specific
    /// write count deterministically. The CANCELLABLE write overload is implemented explicitly, since
    /// production writes with the live stream token.
    /// </summary>
    private sealed class RecordingWriter : IClientStreamWriter<WorkerMessage>
    {
        private readonly object _gate = new();
        private readonly List<WorkerMessage> _writes = [];
        private readonly Dictionary<int, TaskCompletionSource> _countWaiters = [];

        internal IReadOnlyList<WorkerMessage> Writes
        {
            get { lock (_gate) return _writes.ToList(); }
        }

        /// <summary>
        /// Completes once at least <paramref name="count"/> writes were recorded. The bound is a
        /// FAILURE bound: its expiry throws <see cref="TimeoutException"/> and fails the test.
        /// </summary>
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
            List<TaskCompletionSource> ready = [];
            List<int> satisfied = [];

            lock (_gate)
            {
                _writes.Add(message);

                foreach (var (threshold, waiter) in _countWaiters)
                {
                    if (_writes.Count >= threshold)
                    {
                        ready.Add(waiter);
                        satisfied.Add(threshold);
                    }
                }

                foreach (var threshold in satisfied)
                    _countWaiters.Remove(threshold);
            }

            foreach (var waiter in ready)
                waiter.TrySetResult();
        }
    }

    /// <summary>
    /// An <see cref="IAgentRunner"/> recording the provisioning callback the REAL lifecycle hands it,
    /// so a test can reach the EXACT production provisioner path (eager and lazy share it) without any
    /// <c>TestProvisioner</c> override.
    /// </summary>
    private sealed class CapturingRunner : IAgentRunner
    {
        internal Func<string?, CancellationToken, Task>? ConfigProvisioner { get; private set; }

        public void SetConfigProvisioner(Func<string?, CancellationToken, Task>? provisioner) =>
            ConfigProvisioner = provisioner;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ResetSessionAsync(string? model, ReasoningEffort? reasoningEffort, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct) =>
            Task.FromResult(string.Empty);

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
