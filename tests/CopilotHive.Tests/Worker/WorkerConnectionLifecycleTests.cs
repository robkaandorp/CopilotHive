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
/// The focused characterization of the worker's ONE PUBLISHED CONNECTION, driven through the REAL
/// <see cref="WorkerService.RunAsync"/> lifecycle.
/// <para>
/// The lifecycle is driven with a fake <see cref="CallInvoker"/> and a fake duplex stream — no live
/// server, no timing sleeps. Every gate is a <see cref="TaskCompletionSource"/>, and the fake
/// stream's writer implements the CANCELLABLE write overload so a cancelled write unwinds exactly
/// like a real gRPC stream writer.
/// </para>
/// </summary>
public sealed class WorkerConnectionLifecycleTests
{
    private const string LocalWorkerId = "worker-local";
    private const string AssignedWorkerId = "worker-assigned";

    /// <summary>
    /// The accepted-registration lifecycle publishes ONE connection, and that connection — not a
    /// separately mutable id — carries the orchestrator-ASSIGNED identity, its own provisioner (the
    /// SAME instance the agent runner's lazy callback got) and its own stream. Teardown then retires
    /// and unpublishes it BEFORE the stream is disposed, so a new operation fails with the existing
    /// disconnected error while an already-captured connection keeps its identity.
    /// </summary>
    [Fact]
    public async Task RunAsync_PublishesOneConnection_ThenRetiresAndUnpublishesBeforeStreamDisposal()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse
        {
            Accepted = true,
            AssignedWorkerId = AssignedWorkerId,
            OrchestratorVersion = "test",
        });

        var runner = new ProvisionerCapturingRunner();
        var provisionerHarness = new ProvisionerHarness();
        using var service = BuildService(runner, provisionerHarness.Provisioner);

        var requests = new RecordingRequestStream();
        var responses = new ChannelResponseReader();

        // Hoisted so the stream's disposal callback below can observe the ACTUAL connection object
        // (by then it has been unpublished, so reading the field would only ever see null).
        WorkerConnection? connection = null;

        // Observed INSIDE each write: whether a connection was already published and still usable at
        // the moment that write was issued. This makes publish-BEFORE-Ready a real ordering claim
        // rather than a post-hoc read: an implementation that published only after Ready would
        // record `false` for the initial Ready's own write.
        var publishedAtEachWrite = new List<bool>();
        requests.OnWrite = _ =>
            publishedAtEachWrite.Add(GetPublishedConnection(service) is { IsRetired: false });

        // The stream's disposal callback runs DURING disposal, so it can observe whether the
        // connection had already been retired by then — the ordering this test asserts.
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

            // The INITIAL Ready is the first observable proof that the connection was published:
            // its identity is the ASSIGNED one, never the locally configured worker id.
            await requests.WaitForWriteCountAsync(1, TestContext.Current.CancellationToken);
            var ready = requests.Writes[0];
            Assert.Equal(WorkerMessage.PayloadOneofCase.Ready, ready.PayloadCase);
            Assert.Equal(AssignedWorkerId, ready.WorkerId);

            // PUBLISH-BEFORE-READY, observed AT the initial Ready's write: the connection was
            // already published and still usable when that Ready was issued.
            Assert.True(publishedAtEachWrite[0], "The connection must be published before the initial Ready.");

            connection = Assert.IsType<WorkerConnection>(GetPublishedConnection(service));
            Assert.False(connection.IsRetired);
            Assert.Same(provisionerHarness.Provisioner, connection.Provisioner);

            // ONE instance backs BOTH provisioning sites: the runner's lazy callback is the
            // connection's provisioner, and invoking it performs a fetch through THIS connection.
            Assert.NotNull(runner.ConfigProvisioner);
            Assert.Equal(connection.Provisioner!.EnsureProvisionedAsync, runner.ConfigProvisioner);
            Assert.Equal(0, provisionerHarness.FetchCount);
            await runner.ConfigProvisioner!(null, TestContext.Current.CancellationToken);
            Assert.Equal(1, provisionerHarness.FetchCount);

            // REPLACEMENT, not addition: the supplied TestProvisioner is the ONLY provisioner, so
            // the production one was never built and no provisioning RPC went through the client.
            Assert.Equal(0, invoker.WorkerConfigCalls);

            // A bridge send while the connection is live uses ITS identity and ITS stream.
            await service.ReportProgressAsync(
                "task-1", "running", "details", TestContext.Current.CancellationToken);
            await requests.WaitForWriteCountAsync(2, TestContext.Current.CancellationToken);
            Assert.Equal(AssignedWorkerId, requests.Writes[1].WorkerId);
            Assert.Equal("report_progress", requests.Writes[1].ToolRequest.ToolName);
            Assert.True(publishedAtEachWrite[1], "The connection must be published for a bridge send.");

            // EOF ends the loop; the lifecycle then retires and unpublishes the connection.
            responses.TryComplete();
            await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            // RETIRED and UNPUBLISHED — both observed BEFORE the stream was disposed, which is the
            // ordering that keeps a new operation off a stream that is about to go away.
            Assert.True(connection.IsRetired);
            Assert.Null(GetPublishedConnection(service));
            Assert.True(retiredAtStreamDisposal, "The connection must be retired before its stream is disposed.");
            Assert.True(unpublishedAtStreamDisposal, "The connection must be unpublished before its stream is disposed.");
            Assert.Equal(1, streamDisposals);
            Assert.Throws<InvalidOperationException>(() => connection.EnsureUsable());

            // A NEW operation on the retired/unpublished service fails with the EXISTING error.
            var disconnected = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.GetSessionAsync("goal:role", TestContext.Current.CancellationToken));
            Assert.Equal("Not connected to orchestrator", disconnected.Message);

            var sendFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ReportProgressAsync(
                    "task-2", "running", "after-teardown", TestContext.Current.CancellationToken));
            Assert.Equal("Not connected to orchestrator", sendFailure.Message);
        }
        finally
        {
            await loopCts.CancelAsync();
            responses.TryComplete();
            try { await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); }
            catch (Exception) { /* teardown only — never mask the assertion above */ }
        }
    }

    /// <summary>
    /// WITHOUT a supplied provisioner the connection builds the PRODUCTION one, bound to THIS
    /// connection's assigned identity and to a fetch that goes through this connection's checked
    /// access — so the provisioning request carries the assigned id, and a fetch after retirement
    /// fails with the existing disconnected error instead of starting transport.
    /// </summary>
    [Fact]
    public async Task ProductionProvisioner_IsBoundToTheConnection_AndFailsAfterRetirement()
    {
        var invoker = new FakeOrchestratorInvoker(new RegisterResponse
        {
            Accepted = true,
            AssignedWorkerId = AssignedWorkerId,
        });

        var runner = new ProvisionerCapturingRunner();
        using var service = new WorkerService("http://localhost:9999", LocalWorkerId, ["coder"]);
        ReplaceRunner(service, runner);

        var responses = new ChannelResponseReader();
        var stream = new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
            new RecordingRequestStream(), responses,
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
            var connection = await WaitForPublishedConnectionAsync(service, TestContext.Current.CancellationToken);

            // NO TestProvisioner was supplied, so the connection built the production provisioner.
            Assert.NotNull(connection.Provisioner);
            Assert.NotNull(runner.ConfigProvisioner);
            Assert.Same(connection.Provisioner, runner.ConfigProvisioner!.Target);

            // The fetch goes through the CONNECTION's client and carries the ASSIGNED identity.
            await runner.ConfigProvisioner!(null, TestContext.Current.CancellationToken);
            Assert.Equal(1, invoker.WorkerConfigCalls);
            Assert.Equal(AssignedWorkerId, invoker.LastWorkerConfigWorkerId);

            // A NEW fetch after retirement fails with the EXISTING error — and starts no transport.
            connection.Retire();
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => runner.ConfigProvisioner!(null, TestContext.Current.CancellationToken));
            Assert.Equal("Not connected to orchestrator", failure.Message);
            Assert.Equal(1, invoker.WorkerConfigCalls);

            responses.TryComplete();
            await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        finally
        {
            await loopCts.CancelAsync();
            responses.TryComplete();
            try { await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); }
            catch (Exception) { /* teardown only — never mask the assertion above */ }
        }
    }

    /// <summary>
    /// A REJECTED registration publishes NOTHING: no connection is observable afterwards, no stream
    /// was ever opened, and the runner was never given a provisioner callback.
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
    }

    /// <summary>
    /// A send that was ALREADY QUEUED on the send gate when the connection retired cannot write on
    /// it — nor on a replacement. Retirement is checked AFTER the gate is acquired, so the queued
    /// send fails with the EXISTING disconnected error and writes nothing, while the write that
    /// held the gate still completes normally.
    /// </summary>
    [Fact]
    public async Task QueuedSend_ChecksRetirementAfterGateAcquisition_AndWritesNothing()
    {
        var gated = new GatedWriter();
        var stream = new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
            gated,
            new ChannelResponseReader(),
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ => { },
            null!);

        using var service = new WorkerService("http://localhost:9999", LocalWorkerId, ["coder"]);
        var connection = TestConnectionFactory.Attach(service, AssignedWorkerId, stream);

        Task? holder = null;
        Task? queued = null;
        try
        {
            // The holder ENTERS the writer and parks there, holding the gate's only permit.
            holder = service.ReportProgressAsync(
                "task-1", "running", "holder", CancellationToken.None);
            await gated.WaitForEntryAsync(0, TestContext.Current.CancellationToken);

            // The queued send is started while the gate is held, so it cannot yet have run.
            queued = service.ReportNarrativeAsync("task-1", "queued", CancellationToken.None);
            Assert.False(queued.IsCompleted);

            // Retire the connection WHILE the queued send is still waiting for the gate.
            connection.Retire();

            // Release the holder: it completes, returns the permit, and the queued send then acquires
            // the gate — where it must observe retirement rather than writing.
            gated.ReleaseEntry(0);
            await holder.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => queued.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            Assert.Equal("Not connected to orchestrator", failure.Message);

            // Exactly ONE write ever entered: the holder's. The queued send wrote nothing.
            Assert.Equal(1, gated.EnteredCount);
            Assert.False(gated.Overlapped);
        }
        finally
        {
            gated.ReleaseAll();
            foreach (var producer in new[] { holder, queued })
            {
                if (producer is null) continue;
                try { await producer.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); }
                catch (Exception) { /* teardown only — never mask the assertion above */ }
            }
        }
    }

    /// <summary>
    /// An operation that ALREADY captured the connection keeps its captured stream, identity and
    /// outcome even after retirement: the unary path and the writer are not retroactively invalidated
    /// by a later teardown.
    /// </summary>
    [Fact]
    public async Task CapturedConnection_KeepsItsStreamAndIdentity_AfterRetirement()
    {
        var requests = new RecordingRequestStream();
        var stream = new AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage>(
            requests,
            new ChannelResponseReader(),
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ => { },
            null!);

        using var service = new WorkerService("http://localhost:9999", LocalWorkerId, ["coder"]);
        var connection = TestConnectionFactory.Attach(service, AssignedWorkerId, stream);

        // Retirement makes a NEW operation fail...
        connection.Retire();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ReportProgressAsync("task-1", "s", "d", TestContext.Current.CancellationToken));

        // ...while the connection itself still carries the identity and stream it was built with,
        // so an already-started operation is unaffected.
        Assert.Equal(AssignedWorkerId, connection.AssignedId);
        Assert.Same(stream, connection.Stream);
        Assert.Empty(requests.Writes);
    }

    // ── Harness ───────────────────────────────────────────────────────────────
    private static WorkerService BuildService(IAgentRunner runner, WorkerConfigProvisioner provisioner)
    {
        var service = new WorkerService("http://localhost:9999", LocalWorkerId, ["coder"]);
        service.TestProvisioner = provisioner;

        var field = typeof(WorkerService).GetField("_agentRunner", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("WorkerService._agentRunner field not found.");
        if (field.GetValue(service) is IAgentRunner existing)
            existing.DisposeAsync().AsTask().GetAwaiter().GetResult();
        field.SetValue(service, runner);

        return service;
    }

    private static WorkerConnection? GetPublishedConnection(WorkerService service) =>
        (WorkerConnection?)typeof(WorkerService)
            .GetField("_connection", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service);

    /// <summary>
    /// Bounded failsafe wait for the lifecycle to publish its connection. Never an ordering device:
    /// it only converts a lifecycle that never publishes into a named failure instead of a hang.
    /// </summary>
    private static async Task<WorkerConnection> WaitForPublishedConnectionAsync(
        WorkerService service, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            if (GetPublishedConnection(service) is { } connection)
                return connection;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("RunAsync never published a connection.");
            await Task.Delay(1, ct);
        }
    }

    private static void ReplaceRunner(WorkerService service, IAgentRunner runner)
    {
        var field = typeof(WorkerService).GetField("_agentRunner", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("WorkerService._agentRunner field not found.");
        if (field.GetValue(service) is IAgentRunner existing)
            existing.DisposeAsync().AsTask().GetAwaiter().GetResult();
        field.SetValue(service, runner);
    }

    /// <summary>
    /// A gated, overlap-detecting <see cref="IClientStreamWriter{WorkerMessage}"/>: every write
    /// parks inside the fake until its release is set, so a test can hold the production send gate
    /// open and prove where a competing producer waits. All observables are TCS/counter based —
    /// there are no sleeps.
    /// </summary>
    private sealed class GatedWriter : FakeClientStreamWriter<WorkerMessage>
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource> _entries = [];
        private readonly List<(int Index, TaskCompletionSource Waiter)> _entryWaiters = [];
        private bool _writing;
        private bool _overlapped;

        /// <summary>Writes that ENTERED the fake.</summary>
        internal int EnteredCount { get { lock (_gate) return _entries.Count; } }

        /// <summary>True if a second write ever entered while another was still parked.</summary>
        internal bool Overlapped { get { lock (_gate) return _overlapped; } }

        public override Task WriteAsync(WorkerMessage message) => WriteCoreAsync(message, CancellationToken.None);

        protected override Task WriteWithTokenAsync(WorkerMessage message, CancellationToken ct)
            => WriteCoreAsync(message, ct);

        private async Task WriteCoreAsync(WorkerMessage message, CancellationToken ct)
        {
            TaskCompletionSource entry;
            lock (_gate)
            {
                if (_writing)
                    _overlapped = true;
                _writing = true;

                entry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _entries.Add(entry);
                SignalEntryWaiters_Locked();
            }

            try
            {
                await entry.Task.WaitAsync(ct);
            }
            finally
            {
                lock (_gate)
                    _writing = false;
            }
        }

        /// <summary>Completes once the <paramref name="index"/>-th write has entered the fake.</summary>
        internal Task WaitForEntryAsync(int index, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_entries.Count > index)
                    return Task.CompletedTask;
                var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _entryWaiters.Add((index, waiter));
                return waiter.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
        }

        /// <summary>Releases ONE parked write by index.</summary>
        internal void ReleaseEntry(int index)
        {
            TaskCompletionSource entry;
            lock (_gate)
                entry = _entries[index];
            entry.TrySetResult();
        }

        /// <summary>Teardown drain: releases every parked write so awaited sends unwind.</summary>
        internal void ReleaseAll()
        {
            lock (_gate)
            {
                foreach (var entry in _entries)
                    entry.TrySetResult();
            }
        }

        public override Task CompleteAsync() => Task.CompletedTask;

        private void SignalEntryWaiters_Locked()
        {
            for (var i = _entryWaiters.Count - 1; i >= 0; i--)
            {
                var (index, waiter) = _entryWaiters[i];
                if (_entries.Count > index)
                {
                    waiter.TrySetResult();
                    _entryWaiters.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>
    /// A <see cref="CallInvoker"/> answering exactly the two unary RPCs the lifecycle reaches —
    /// <c>Register</c> and <c>GetWorkerConfig</c> — so the REAL <see cref="WorkerService.RunAsync"/>
    /// runs without a live server. Any other call is a bug in the fixture and throws loudly.
    /// </summary>
    private sealed class FakeOrchestratorInvoker(RegisterResponse registerResponse) : CallInvoker
    {
        private int _registerCalls;
        private int _workerConfigCalls;
        private string? _lastWorkerConfigWorkerId;

        internal int RegisterCalls => Volatile.Read(ref _registerCalls);
        internal int WorkerConfigCalls => Volatile.Read(ref _workerConfigCalls);

        /// <summary>The <c>worker_id</c> of the most recent <c>GetWorkerConfig</c> request.</summary>
        internal string? LastWorkerConfigWorkerId => Volatile.Read(ref _lastWorkerConfigWorkerId);

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException($"Unexpected blocking call {method.FullName}.");

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            object payload = method.FullName switch
            {
                "/copilothive.HiveOrchestrator/Register" => Respond(ref _registerCalls, registerResponse),
                "/copilothive.HiveOrchestrator/GetWorkerConfig" => RespondWorkerConfig(request),
                _ => throw new NotSupportedException($"Unexpected unary call {method.FullName}."),
            };

            return new AsyncUnaryCall<TResponse>(
                Task.FromResult((TResponse)payload),
                Task.FromResult(new Metadata()),
                () => new Status(StatusCode.OK, string.Empty),
                () => new Metadata(),
                () => { });
        }

        private object RespondWorkerConfig<TRequest>(TRequest request)
        {
            Interlocked.Increment(ref _workerConfigCalls);
            Volatile.Write(
                ref _lastWorkerConfigWorkerId,
                (request as GetWorkerConfigRequest)?.WorkerId);
            return new GetWorkerConfigResponse();
        }

        private static object Respond<T>(ref int counter, T response)
        {
            Interlocked.Increment(ref counter);
            return response!;
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
                $"Unexpected duplex call {method.FullName} — the fixture supplies the stream via WorkStreamFactory.");
    }

    /// <summary>
    /// Records every <see cref="WorkerMessage"/> the lifecycle writes and lets a test await a
    /// specific write count deterministically. Implements the cancellable write overload
    /// EXPLICITLY, since the default interface method throws for a cancellable token and the
    /// lifecycle writes with the live stream token.
    /// </summary>
    private sealed class RecordingRequestStream : IClientStreamWriter<WorkerMessage>
    {
        private readonly object _gate = new();
        private readonly List<WorkerMessage> _writes = [];
        private readonly Dictionary<int, TaskCompletionSource> _waiters = [];

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

        internal Task WaitForWriteCountAsync(int count, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_writes.Count >= count)
                    return Task.CompletedTask;
                if (!_waiters.TryGetValue(count, out var waiter))
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters[count] = waiter;
                }
                return waiter.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
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
            lock (_gate)
            {
                _writes.Add(message);
                foreach (var (threshold, waiter) in _waiters)
                {
                    if (_writes.Count >= threshold)
                        ready.Add(waiter);
                }
                foreach (var waiter in ready)
                    _waiters.Remove(_waiters.First(kv => kv.Value == waiter).Key);
            }

            foreach (var waiter in ready)
                waiter.TrySetResult();
        }
    }

    /// <summary>
    /// An <see cref="IAgentRunner"/> that records the provisioning callback the lifecycle hands it,
    /// so a test can prove the connection's provisioner is the SAME instance behind both
    /// provisioning sites.
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
