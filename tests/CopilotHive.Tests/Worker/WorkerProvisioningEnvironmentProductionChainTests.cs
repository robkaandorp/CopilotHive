using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;
using CopilotHive.Workers;

using Grpc.Core;

using Microsoft.Extensions.AI;

using System.Reflection;
using System.Threading.Channels;

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
/// <b>Removal demonstration.</b> Building B's service with FRESH provenance over the same fake
/// environment (the stale-operator regression) makes B's snapshot capture A's provisioned values, so
/// the "A-owned value is CLEARED" assertion observes A's value and fails.
/// </para>
/// <para>
/// No real credentials, no process-environment mutation and no network: the environment is a fake
/// in-memory dictionary and every RPC is answered by a fake invoker. Every gate is a
/// <see cref="TaskCompletionSource"/> or a counted write — there are NO sleep-ordered waits, and every
/// started task is joined in a <c>finally</c>.
/// </para>
/// </summary>
[Collection("ConsoleOutput")]
public sealed class WorkerProvisioningEnvironmentProductionChainTests
{
    private const string LocalWorkerId = "worker-local";
    private const string AssignedIdA = "worker-assigned-a";
    private const string AssignedIdB = "worker-assigned-b";
    private const string FixtureModel = "copilot/fixture-model";

    /// <summary>Generous failsafe bound; never an ordering device.</summary>
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(30);

    private const string OperatorOllamaUrl = "http://operator:11434";
    private const string OperatorGithubToken = "operator-original-github-token";
    private const string OperatorConfigRepoUrl = "https://github.com/operator/repo.git";

    /// <summary>
    /// TWO SEQUENTIAL ATTEMPTS, ONE SHARED PROVENANCE — the whole contract in one flow.
    /// </summary>
    [Fact]
    public async Task TwoSequentialAttempts_SharedProvenance_ReplaceAndClearUnderEachAttemptsIdentity()
    {
        // The FAKE process environment plus the ONE provenance object Program.cs would create
        // outside its retry loop. Nothing here touches the real process environment.
        var env = new FakeEnv(
            (WorkerConfigProvisioner.OllamaUrlVar, OperatorOllamaUrl),
            (WorkerConfigProvisioner.GitHubTokenVar, OperatorGithubToken),
            (WorkerConfigProvisioner.ConfigRepoUrlVar, OperatorConfigRepoUrl));
        var shared = new WorkerProvisioningEnvironment(env.Read, env.Write);

        // ── Attempt A ─────────────────────────────────────────────────────────────
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

        using var serviceA = attemptA;
        using var loopCtsA = new CancellationTokenSource();
        var runA = Task.CompletedTask;

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

            // ── Attempt B: a FRESH service through the SAME attempt-construction path ──
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
            using var serviceB = BuildAttempt(shared, runnerB, invokerB, readerB, writerB);
            using var loopCtsB = new CancellationTokenSource();
            var runB = Task.CompletedTask;

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
                Assert.NotSame(connectionA.Provisioner, connectionB.Provisioner);

                Assert.NotNull(runnerB.ConfigProvisioner);
                await runnerB.ConfigProvisioner!(FixtureModel, TestContext.Current.CancellationToken);

                // B FETCHED THROUGH B: its own client and its own accepted identity.
                Assert.Equal(1, invokerB.WorkerConfigCalls);
                Assert.Equal(AssignedIdB, invokerB.LastWorkerConfigWorkerId);
                Assert.Equal(fetchCallsBeforeRetiredCallback, invokerA.WorkerConfigCalls);

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
                Assert.Equal("https://github.com/org/attempt-a.git", connectionA.Provisioner.ProvisionedConfigRepoUrl);

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
                await loopCtsB.CancelAsync();
                readerB.Complete();
                await ObserveForTeardownAsync(runB);
            }
        }
        finally
        {
            await loopCtsA.CancelAsync();
            readerA.Complete();
            await ObserveForTeardownAsync(runA);
        }
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
            // Teardown only: the attempt may fault (a transport failure, a cancelled teardown). Its
            // real outcome was asserted in the try body; rethrowing here would mask that assertion.
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
    /// The orchestrator-message reader for ONE attempt. It can END the loop three ways: an
    /// availability FAULT (the transport failure under test), a clean EOF, or a scripted message —
    /// every one of them a deterministic signal, never a delay.
    /// </summary>
    private sealed class ScriptedReader : IAsyncStreamReader<OrchestratorMessage>
    {
        private readonly Channel<OrchestratorMessage> _channel = Channel.CreateUnbounded<OrchestratorMessage>();
        private readonly TaskCompletionSource _failSignalled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private volatile Exception? _failure;

        public OrchestratorMessage Current { get; private set; } = null!;

        /// <summary>Signals a transport fault; the next <c>MoveNext</c> throws it.</summary>
        internal void Fail(Exception exception)
        {
            _failure = exception;
            _failSignalled.TrySetResult();
        }

        /// <summary>Ends the stream cleanly (EOF).</summary>
        internal void Complete() => _channel.Writer.TryComplete();

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            var readTask = _channel.Reader.WaitToReadAsync(cancellationToken).AsTask();
            await Task.WhenAny(readTask, _failSignalled.Task);

            if (_failure is { } failure)
                throw failure;

            if (!await readTask)
                return false;

            if (!_channel.Reader.TryRead(out var message))
                return false;

            Current = message;
            return true;
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
