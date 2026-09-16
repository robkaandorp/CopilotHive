using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;
using CopilotHive.Workers;

using Grpc.Core;

using Microsoft.Extensions.AI;

using System.Reflection;
using System.Runtime.CompilerServices;

using GrpcWorkerRole = CopilotHive.Shared.Grpc.WorkerRole;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// THE PROCESS-LIFETIME CONTRACT, END TO END: <b>ONE</b> <see cref="WorkerService"/> and its
/// <b>ONE</b> real <see cref="SharpCoderRunner"/> serve TWO REAL, SEQUENTIAL
/// <see cref="WorkerService.RunAsync"/> attempts — exactly the shape <c>Program.cs</c> now has, where
/// the service is constructed once outside the attempt loop and disposed once after it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real here.</b> The runner is the production <see cref="SharpCoderRunner"/> (not a
/// double), its <c>ClientCreationSeam</c> supplies DISTINCT fake chat clients per attempt (never the
/// precreated-client constructor), and each attempt runs the whole accepted-registration lifecycle
/// over the existing fake call-invoker / fake duplex-stream seams: registration, publication, the
/// initial Ready, a GENUINE assignment (a real <see cref="TaskExecutor"/> run through a real
/// <c>CodingAgent</c> turn against the fake chat client), the completion write, and the per-assignment
/// Ready — followed by attempt A's controlled reader FAULT and attempt B's clean EOF.
/// </para>
/// <para>
/// <b>What it pins.</b> (a) attempt A's fault fully drains and attempt B is then admitted on the SAME
/// service and the SAME runner; (b) NO intermediate runner disposal — a disposed runner's client
/// lifecycle gate would make attempt B's lazy client creation fail; (c) NO replay of A's completion or
/// transport use by B: A's stream never moves again and B's stream only ever carries B's own assigned
/// identity and task; (d) B's assignment is GENUINE, not an idle cancel — it creates its own client,
/// carries its own model, provisions through its OWN connection identity, and writes its own terminal
/// completion; (e) the final disposal happens exactly once and a run afterwards is refused.
/// </para>
/// <para>
/// Every gate is a <see cref="TaskCompletionSource"/>, a counted write or a channel fault/EOF: there
/// are NO sleeps and no polling, every await has a bounded failsafe, and every started producer is
/// joined in a <c>finally</c> even on assertion-failure paths.
/// </para>
/// </remarks>
[Collection("ConsoleOutput")]
public sealed class WorkerServiceSequentialRunReuseTests
{
    private const string LocalWorkerId = "worker-local";
    private const string AssignedIdA = "worker-sequential-a";
    private const string AssignedIdB = "worker-sequential-b";
    private const string ModelA = "fixture-provider/fixture-model-a";
    private const string ModelB = "fixture-provider/fixture-model-b";
    private const string ConfigRepoUrl = "https://github.com/org/config-repo.git";

    /// <summary>A bounded FAILURE failsafe for every await; never an ordering device.</summary>
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(30);

    /// <summary>
    /// TWO REAL SEQUENTIAL RUNS ON ONE SERVICE AND ONE REAL RUNNER — the whole contract in one flow.
    /// </summary>
    [Fact]
    public async Task TwoSequentialRuns_OneServiceAndRunner_ExecuteGenuineAssignmentsOnDistinctClients()
    {
        // The process's in-memory environment provenance (Program.cs creates ONE for the process):
        // the production provisioner's environment writes land here, never in the real process env.
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        var provenance = new WorkerProvisioningEnvironment(
            name => environment.TryGetValue(name, out var value) ? value : null,
            (name, value) =>
            {
                if (value is null) environment.Remove(name);
                else environment[name] = value;
            });

        var configRepoDir = CreateTempDir();
        var launcher = new FakeGitLauncher(HealthyRepoHandler(configRepoDir));
        using var processRunner = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        // THE REAL RUNNER, shared by both attempts.
        var runner = new SharpCoderRunner(configRepoDir);
        var clients = new List<GatedChatClient>();
        var seamModels = new List<string?>();
        var agentOptions = new List<SharpCoder.AgentOptions>();

        runner.ClientCreationSeam = model =>
        {
            lock (clients)
            {
                seamModels.Add(model);
                var client = new GatedChatClient();
                clients.Add(client);
                return client;
            }
        };
        runner.OnAgentOptionsCreated = options =>
        {
            lock (agentOptions)
                agentOptions.Add(options);
        };

        // ONE service for the whole "process", built through the EXACT internal attempt-construction
        // path Program.cs uses (shared provenance, no TestProvisioner override), with THE REAL RUNNER
        // installed through the same reflection seam the existing WorkerService fixtures use.
        var service = new WorkerService(
            "http://localhost:9999", LocalWorkerId, ["coder"], provenance, configRepoDir);
        InstallRunner(service, runner);

        var invokerA = new ScriptedInvoker(RegisterFor(AssignedIdA), ConfigRepoUrl, "ghp_attempt_a");
        var invokerB = new ScriptedInvoker(RegisterFor(AssignedIdB), ConfigRepoUrl, "ghp_attempt_b");

        var requestsA = new RecordingRequestStream();
        var responsesA = new FaultingReader();
        var requestsB = new RecordingRequestStream();
        var responsesB = new FaultingReader();

        var streamDisposalsA = 0;
        var streamDisposalsB = 0;
        var streamA = BuildStream(requestsA, responsesA, () => Interlocked.Increment(ref streamDisposalsA));
        var streamB = BuildStream(requestsB, responsesB, () => Interlocked.Increment(ref streamDisposalsB));

        service.CallInvokerFactory = () => invokerA;
        service.WorkStreamFactory = (_, _) => streamA;

        using var loopCts = new CancellationTokenSource();
        Task<WorkerRunOutcome>? runA = null;
        Task<WorkerRunOutcome>? runB = null;
        var serviceDisposed = false;

        try
        {
            // ══ ATTEMPT A ═══════════════════════════════════════════════════════════
            runA = service.RunAsync(loopCts.Token);

            // The initial Ready proves publication through the REAL lifecycle.
            await requestsA.WaitForWriteCountAsync(1, TestContext.Current.CancellationToken);
            Assert.Equal(WorkerMessage.PayloadOneofCase.Ready, requestsA.Writes[0].PayloadCase);
            Assert.Equal(AssignedIdA, requestsA.Writes[0].WorkerId);

            var connectionA = Assert.IsType<WorkerConnection>(GetPublishedConnection(service));
            Assert.Equal(AssignedIdA, connectionA.AssignedId);
            Assert.NotNull(connectionA.Provisioner);

            // A GENUINE ASSIGNMENT with A's own model.
            responsesA.Push(Assignment("task-a", AssignedIdA, ModelA));

            // A's lazy client creation is reached with A's model — proof the assignment really ran.
            var clientA = await WaitForClientAsync(clients, 0, TestContext.Current.CancellationToken);
            Assert.Equal(ModelA, seamModels[0]);

            clientA.Release();

            // A's assignment completes: it writes its terminal Complete and then its own Ready.
            await requestsA.WaitForWriteCountAsync(2, TestContext.Current.CancellationToken);
            var completeA = Assert.Single(
                requestsA.Writes, w => w.PayloadCase == WorkerMessage.PayloadOneofCase.Complete);
            Assert.Equal("task-a", completeA.Complete.TaskId);
            Assert.Equal(AssignedIdA, completeA.WorkerId);
            await requestsA.WaitForReadyCountAsync(2, TestContext.Current.CancellationToken);

            // A's production provisioner fetched through A'S OWN connection identity: ONCE from the
            // EAGER per-assignment site and ONCE from the LAZY callback before the assignment's first
            // client creation — the two sites share this one connection-owned provisioner.
            Assert.Equal(2, invokerA.WorkerConfigCalls);
            Assert.Equal(AssignedIdA, invokerA.LastWorkerConfigWorkerId);
            Assert.Equal(0, invokerB.WorkerConfigCalls);

            // The real agent turn received the production-built options for the assignment.
            var optionsA = Assert.Single(agentOptions);
            Assert.Equal(500, optionsA.MaxSteps);

            // CONTROLLED READER FAULT: the transport fails, so the real loop drains, retires and
            // unpublishes A's connection and RunAsync faults with the retryable RpcException category.
            var transportFailure = new RpcException(new Status(StatusCode.Unavailable, "transport lost"));
            responsesA.Fail(transportFailure);

            var thrownA = await Assert.ThrowsAsync<RpcException>(
                () => runA.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Same(transportFailure, thrownA);

            // A is FULLY wound down: retired, unpublished, transport disposed, no live write.
            Assert.True(connectionA.IsRetired);
            Assert.Null(GetPublishedConnection(service));
            Assert.Equal(1, Volatile.Read(ref streamDisposalsA));
            var writesAfterADrain = requestsA.Writes.Count;
            Assert.Equal(0, connectionA.PendingToolResponseCount);

            // NO INTERMEDIATE RUNNER DISPOSAL: the runner that served A is still live and undisposed,
            // and A's client is still owned by it (idle backoff must not tear the client down).
            Assert.Same(runner, GetRunner(service));
            Assert.Equal(0, ReadRunnerDisposedFlag(runner));
            Assert.Equal(0, clientA.DisposeCount);

            // ══ ATTEMPT B — the SAME service, the SAME runner, a FRESH connection ═══
            service.CallInvokerFactory = () => invokerB;
            service.WorkStreamFactory = (_, _) => streamB;

            runB = service.RunAsync(loopCts.Token);

            await requestsB.WaitForWriteCountAsync(1, TestContext.Current.CancellationToken);
            Assert.Equal(AssignedIdB, requestsB.Writes[0].WorkerId);

            var connectionB = Assert.IsType<WorkerConnection>(GetPublishedConnection(service));
            Assert.Equal(AssignedIdB, connectionB.AssignedId);
            // A FRESH connection object with its OWN provisioner: a retired connection is never reused.
            Assert.NotSame(connectionA, connectionB);
            Assert.NotSame(connectionA.Provisioner, connectionB.Provisioner);

            // B's assignment is GENUINE — its own task, its own model.
            responsesB.Push(Assignment("task-b", AssignedIdB, ModelB));

            // B's reset disposed A's client (the runner is reused, not recreated) and B's lazy
            // creation reached the seam with B's OWN model.
            var clientB = await WaitForClientAsync(clients, 1, TestContext.Current.CancellationToken);
            Assert.Equal(ModelB, seamModels[1]);
            Assert.NotSame(clientA, clientB);
            Assert.Equal(1, clientA.DisposeCount);

            clientB.Release();

            await requestsB.WaitForWriteCountAsync(2, TestContext.Current.CancellationToken);
            var completeB = Assert.Single(
                requestsB.Writes, w => w.PayloadCase == WorkerMessage.PayloadOneofCase.Complete);
            Assert.Equal("task-b", completeB.Complete.TaskId);
            Assert.Equal(AssignedIdB, completeB.WorkerId);
            await requestsB.WaitForReadyCountAsync(2, TestContext.Current.CancellationToken);

            // B PROVISIONED THROUGH ITS OWN CONNECTION: its own identity, and A's client saw nothing.
            // Same two-site shape as A — the eager site plus the lazy callback — on B's OWN invoker.
            Assert.Equal(2, invokerB.WorkerConfigCalls);
            Assert.Equal(AssignedIdB, invokerB.LastWorkerConfigWorkerId);
            Assert.Equal(2, invokerA.WorkerConfigCalls);

            // NO A REPLAY AND NO A TRANSPORT USE BY B: A's stream never moved again, and B's stream
            // only ever carried B's own identity and task.
            Assert.Equal(writesAfterADrain, requestsA.Writes.Count);
            Assert.All(requestsB.Writes, w => Assert.Equal(AssignedIdB, w.WorkerId));
            Assert.DoesNotContain(
                requestsB.Writes,
                w => w.PayloadCase == WorkerMessage.PayloadOneofCase.Complete && w.Complete.TaskId == "task-a");

            // Both assignments really ran as separate agent turns, each with its own built options.
            Assert.Equal(2, agentOptions.Count);
            Assert.NotSame(agentOptions[0], agentOptions[1]);

            // CLEAN EOF ends B with the existing WorkStreamEnded outcome.
            responsesB.TryComplete();
            Assert.Equal(
                WorkerRunOutcome.WorkStreamEnded,
                await runB.WaitAsync(Failsafe, TestContext.Current.CancellationToken));

            Assert.True(connectionB.IsRetired);
            Assert.Null(GetPublishedConnection(service));
            Assert.Equal(1, Volatile.Read(ref streamDisposalsB));

            // ══ THE ONE FINAL DISPOSAL ═════════════════════════════════════════════
            Assert.Equal(0, ReadRunnerDisposedFlag(runner));
            Assert.Equal(0, clientB.DisposeCount);

            service.Dispose();
            serviceDisposed = true;

            // Happened EXACTLY ONCE: a repeat is a no-op — the runner is not interacted with again.
            Assert.Equal(1, ReadRunnerDisposedFlag(runner));
            Assert.Equal(1, clientB.DisposeCount);
            service.Dispose();
            Assert.Equal(1, ReadRunnerDisposedFlag(runner));
            Assert.Equal(1, clientB.DisposeCount);

            // A run after final disposal is REFUSED with the existing .NET disposal category.
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => service.RunAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            await loopCts.CancelAsync();
            responsesA.TryComplete();
            responsesB.TryComplete();
            ReleaseAll(clients);
            await JoinAllAsync(("attempt A RunAsync", runA), ("attempt B RunAsync", runB));
            if (!serviceDisposed)
                TryDispose(service);
            TryDelete(configRepoDir);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Harness.
    // ══════════════════════════════════════════════════════════════════════════

    private static RegisterResponse RegisterFor(string assignedId) =>
        new() { Accepted = true, AssignedWorkerId = assignedId, OrchestratorVersion = "test" };

    private static OrchestratorMessage Assignment(string taskId, string workerId, string model) => new()
    {
        Assignment = new TaskAssignment
        {
            TaskId = taskId,
            GoalId = $"goal-{taskId}",
            GoalDescription = $"desc for {taskId}",
            Prompt = "do the thing",
            Role = GrpcWorkerRole.Coder,
            Model = model,
        },
    };

    private static AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> BuildStream(
        RecordingRequestStream requests, FaultingReader responses, Action onDisposed) =>
        new(
            requests, responses,
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ => onDisposed(),
            null!);

    /// <summary>Reflects the real published-connection field (observation only).</summary>
    private static WorkerConnection? GetPublishedConnection(WorkerService service) =>
        (WorkerConnection?)typeof(WorkerService)
            .GetField("_connection", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service);

    /// <summary>Reads the service's agent runner (observation only).</summary>
    private static IAgentRunner GetRunner(WorkerService service) =>
        (IAgentRunner)typeof(WorkerService)
            .GetField("_agentRunner", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service)!;

    /// <summary>
    /// Installs the supplied runner through the private field the existing WorkerService fixtures
    /// use, so the REAL <see cref="WorkerService.RunAsync"/> drives THE REAL runner.
    /// </summary>
    private static void InstallRunner(WorkerService service, IAgentRunner runner)
    {
        var field = typeof(WorkerService).GetField("_agentRunner", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("WorkerService._agentRunner field not found.");

        if (field.GetValue(service) is IAgentRunner existing)
            existing.DisposeAsync().AsTask().GetAwaiter().GetResult();

        field.SetValue(service, runner);
    }

    /// <summary>
    /// Reads the real runner's one-way disposal flag (observation only). A disposed runner is
    /// observable here as 1, so "the runner was not disposed between attempts" is a direct claim.
    /// </summary>
    private static int ReadRunnerDisposedFlag(SharpCoderRunner runner) =>
        (int)typeof(SharpCoderRunner)
            .GetField("_disposed", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(runner)!;

    /// <summary>Waits until the seam has produced at least <paramref name="index"/>+1 clients.</summary>
    private static async Task<GatedChatClient> WaitForClientAsync(
        List<GatedChatClient> clients, int index, CancellationToken ct)
    {
        var deadline = Task.Delay(Failsafe, ct);
        while (true)
        {
            lock (clients)
            {
                if (clients.Count > index)
                    return clients[index];
            }

            if (deadline.IsCompleted)
                throw new TimeoutException($"No chat client #{index} was created within the failsafe bound.");

            await Task.WhenAny(Task.Delay(1, ct), deadline);
        }
    }

    private static void ReleaseAll(List<GatedChatClient> clients)
    {
        lock (clients)
            foreach (var client in clients)
                client.Release();
    }

    /// <summary>Joins every started run, each under its own bounded wait and independently.</summary>
    private static async Task JoinAllAsync(params (string Name, Task? Producer)[] producers)
    {
        List<Exception> failures = [];
        foreach (var (name, producer) in producers)
        {
            if (producer is null)
                continue;

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

        if (failures.Count == 1)
            throw failures[0];
        if (failures.Count > 1)
            throw new AggregateException("Teardown could not join every original task.", failures);
    }

    private static void TryDispose(WorkerService service)
    {
        try
        {
            service.Dispose();
        }
        catch
        {
            // Best effort: a teardown fault must never mask the test's own failure.
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sequential-run-{Guid.NewGuid():N}");
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

    /// <summary>A git handler for a HEALTHY repo whose origin is the credential-free provisioned URL.</summary>
    private static Func<IReadOnlyList<string>, GitProcessResult> HealthyRepoHandler(string configRepoDir) =>
        tokens =>
        {
            if (Matches(tokens, "rev-parse", "--is-inside-work-tree"))
                return new GitProcessResult(0, "true\n", "");
            if (Matches(tokens, "rev-parse", "--show-toplevel"))
                return new GitProcessResult(0, configRepoDir + "\n", "");
            if (Matches(tokens, "remote", "get-url", "origin"))
                return new GitProcessResult(0, ConfigRepoUrl + "\n", "");
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

    /// <summary>
    /// A fake chat client that signals when the agent turn starts streaming and then PARKS until the
    /// test releases it — the deterministic window in which the assignment is genuinely in flight.
    /// Disposal is counted so the runner's client ownership (and its final teardown) is observable.
    /// </summary>
    private sealed class GatedChatClient : IChatClient
    {
        private readonly TaskCompletionSource<bool> _streamingEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCount;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        internal void Release() => _release.TrySetResult(true);

        public ChatClientMetadata Metadata => new("gated", null, "stub");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."))
            {
                FinishReason = ChatFinishReason.Stop,
            });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            _streamingEntered.TrySetResult(true);
            return StreamAsync(ct);
        }

        private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            await _release.Task.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Done.")]);
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop, Role = ChatRole.Assistant };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    /// <summary>
    /// The per-attempt orchestrator reader: pushes messages, supports a CONTROLLED reader FAULT (the
    /// retryable transport failure attempt A must drain from) and a clean EOF, and lets a test await
    /// how many messages the real loop has consumed.
    /// </summary>
    private sealed class FaultingReader : IAsyncStreamReader<OrchestratorMessage>
    {
        private readonly object _gate = new();
        private readonly List<OrchestratorMessage> _pending = [];
        private readonly List<TaskCompletionSource> _consumedWaiters = [];
        private Exception? _fault;
        private bool _completed;
        private int _consumed;

        public OrchestratorMessage Current { get; private set; } = null!;

        internal void Push(OrchestratorMessage message)
        {
            lock (_gate)
            {
                _pending.Add(message);
                ReleaseLocked();
            }
        }

        /// <summary>Arms a ONE-SHOT reader fault: the waiting (or next) read surfaces it verbatim.</summary>
        internal void Fail(Exception fault)
        {
            lock (_gate)
            {
                _fault ??= fault;
                ReleaseLocked();
            }
        }

        internal void TryComplete()
        {
            lock (_gate)
            {
                _completed = true;
                ReleaseLocked();
            }
        }

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            while (true)
            {
                TaskCompletionSource waiter;
                lock (_gate)
                {
                    if (_fault is { } fault)
                        throw fault;

                    if (_pending.Count > 0)
                    {
                        Current = _pending[0];
                        _pending.RemoveAt(0);
                        _consumed++;
                        ReleaseLocked();
                        return true;
                    }

                    if (_completed)
                        return false;

                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _consumedWaiters.Add(waiter);
                }

                await using var registration = cancellationToken.Register(
                    static state => ((TaskCompletionSource)state!).TrySetCanceled(), waiter);

                await waiter.Task;
            }
        }

                private void ReleaseLocked()
        {
            if (_consumedWaiters.Count == 0)
                return;

            var waiters = _consumedWaiters.ToArray();
            _consumedWaiters.Clear();
            foreach (var waiter in waiters)
                waiter.TrySetResult();
        }
    }

    /// <summary>
    /// Records every <see cref="WorkerMessage"/> a stream writes and lets a test await a specific write
    /// or Ready count deterministically. The CANCELLABLE write overload is implemented explicitly,
    /// since production writes with the live stream token.
    /// </summary>
    private sealed class RecordingRequestStream : IClientStreamWriter<WorkerMessage>
    {
        private readonly object _gate = new();
        private readonly List<WorkerMessage> _writes = [];
        private readonly Dictionary<int, TaskCompletionSource> _countWaiters = [];
        private readonly Dictionary<int, TaskCompletionSource> _readyWaiters = [];
        private int _readyCount;

        internal IReadOnlyList<WorkerMessage> Writes
        {
            get { lock (_gate) return [.. _writes]; }
        }

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
            List<TaskCompletionSource> ready = [];
            lock (_gate)
            {
                _writes.Add(message);
                if (message.PayloadCase == WorkerMessage.PayloadOneofCase.Ready)
                    _readyCount++;

                foreach (var (threshold, waiter) in _countWaiters)
                {
                    if (_writes.Count >= threshold)
                        ready.Add(waiter);
                }
                _countWaiters.Clear();

                foreach (var (threshold, waiter) in _readyWaiters)
                {
                    if (_readyCount >= threshold)
                        ready.Add(waiter);
                }
                _readyWaiters.Clear();
            }

            foreach (var waiter in ready)
                waiter.TrySetResult();
        }
    }

    /// <summary>
    /// A <see cref="CallInvoker"/> answering the unary RPCs the REAL accepted-registration lifecycle
    /// reaches — <c>Register</c>, <c>GetWorkerConfig</c>, <c>GetSession</c>, <c>SaveSession</c> and
    /// <c>Heartbeat</c> — recording the provisioning requests so a test can prove WHICH attempt's
    /// identity and client performed a fetch. Any other call is a fixture bug and throws loudly.
    /// </summary>
    private sealed class ScriptedInvoker(
        RegisterResponse registerResponse, string configRepoUrl, string githubToken) : CallInvoker
    {
        private int _registerCalls;
        private int _workerConfigCalls;
        private string? _lastWorkerConfigWorkerId;

        internal int WorkerConfigCalls => Volatile.Read(ref _workerConfigCalls);
        internal string? LastWorkerConfigWorkerId => Volatile.Read(ref _lastWorkerConfigWorkerId);

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
            return registerResponse;
        }

        private object RespondWorkerConfig<TRequest>(TRequest request)
        {
            Interlocked.Increment(ref _workerConfigCalls);
            Volatile.Write(ref _lastWorkerConfigWorkerId, (request as GetWorkerConfigRequest)?.WorkerId);
            return new GetWorkerConfigResponse
            {
                GithubToken = githubToken,
                LlmProvider = "copilot",
                ConfigRepoUrl = configRepoUrl,
            };
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
}
