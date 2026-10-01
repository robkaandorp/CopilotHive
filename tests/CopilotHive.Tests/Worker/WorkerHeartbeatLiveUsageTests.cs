using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;
using CopilotHive.Workers;

using Grpc.Core;

using Microsoft.Extensions.AI;

using System.Reflection;

using SharpCoder;

using DomainWorkerRole = CopilotHive.Workers.WorkerRole;
using GrpcWorkerRole = CopilotHive.Shared.Grpc.WorkerRole;
using SharpUsageSource = SharpCoder.UsageSource;
using SharpUsageEntry = SharpCoder.UsageEntry;
using SharpUsageSummary = SharpCoder.UsageSummary;
using SharpTokenUsage = SharpCoder.TokenUsage;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// The WORKER SIDE of the live-usage transport: one heartbeat tick carries the RUNNING task's usage
/// accumulated so far, tagged with the task id that same request names — and only when the runner
/// reports usage for a snapshot that still belongs to that task.
/// </summary>
/// <remarks>
/// <para>
/// Everything here drives the REAL per-tick method the production heartbeat loop calls
/// (<see cref="WorkerService.SendHeartbeatOnceForTestAsync"/>) through a connection whose gRPC client
/// is materialised from a fake call invoker, so the assertions read the request that actually went
/// out. No interval is ever awaited, there are no sleeps and no polling: every wait is a
/// <see cref="TaskCompletionSource"/> or a counted write.
/// </para>
/// <para>
/// The usage snapshot is supplied by a recording <see cref="IAgentRunner"/> double that ALSO records
/// the task id observable at each of its usage boundaries — which is what makes the ordering claim
/// (reset before publish) and the transition-consistency claim (a task id that moved between the
/// capture and the re-read leaves the usage ABSENT) provable rather than merely plausible.
/// </para>
/// </remarks>
[Collection("ConsoleOutput")]
public sealed class WorkerHeartbeatLiveUsageTests
{
    /// <summary>A bounded FAILURE failsafe; never an ordering device.</summary>
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(30);

    private const string LocalWorkerId = "worker-local";
    private const string AssignedWorkerId = "worker-assigned";

    // ── Recording test doubles ───────────────────────────────────────────────

    /// <summary>
    /// A recording <see cref="IAgentRunner"/> that REPORTS usage: every usage boundary is recorded
    /// together with the task id observable at that instant, so a test can assert ordering rather
    /// than infer it.
    /// </summary>
    /// <remarks>
    /// It lists <see cref="IAgentRunner"/> DIRECTLY rather than declaring the members on a base that
    /// already implements the interface: a derived declaration does not re-implement an interface
    /// member the base bound, so the base's binding (the interface DEFAULT) would keep being used.
    /// </remarks>
    private sealed class RecordingUsageRunner : IAgentRunner
    {
        private readonly object _gate = new();
        private readonly List<string?> _resetObservedTaskIds = [];

        /// <summary>
        /// The task id observable at the moment of each <see cref="ResetTaskUsage"/> call, in call
        /// order. This is the ordering evidence: the FIRST entry must be whatever the field held
        /// BEFORE this assignment's id was published.
        /// </summary>
        internal IReadOnlyList<string?> ResetObservedTaskIds
        {
            get { lock (_gate) return [.. _resetObservedTaskIds]; }
        }

        /// <summary>How many times production asked this runner for a usage snapshot.</summary>
        internal int GetUsageCount => Volatile.Read(ref _getUsageCount);

        private int _getUsageCount;

        /// <summary>Reads the service field the heartbeat reads, so a reset can observe publication.</summary>
        internal Func<string?>? TaskIdProbe { get; set; }

        /// <summary>
        /// Invoked INSIDE <see cref="GetTaskUsage"/>, after the usage was chosen and before it is
        /// returned — the deterministic interleaving point for a task transition.
        /// </summary>
        internal Action? OnGetTaskUsage { get; set; }

        /// <summary>The thrown-or-returned value of <see cref="GetTaskUsage"/>.</summary>
        internal Func<SharpUsageSummary?>? UsageProvider { get; set; } = () => ReportedUsage;

        /// <summary>The usage this runner reports for the current task; non-null by design here.</summary>
        internal static SharpUsageSummary ReportedUsage { get; } = new(
        [
            new SharpUsageEntry(SharpUsageSource.Agent, "model-a", new SharpTokenUsage
            {
                InputTokens = 4_321,
                OutputTokens = 321,
                CachedInputTokens = 21,
                ReasoningTokens = 3,
                Calls = 2,
                CachedInputReportedCalls = 2,
                ReasoningReportedCalls = 2,
                Estimated = new EstimatedTokenBreakdown
                {
                    SystemPrompt = 11,
                    ToolDefinitions = 12,
                    UserText = 13,
                    AssistantText = 14,
                    ToolCalls = 15,
                    ToolResults = 16,
                    Reasoning = 17,
                    Images = 18,
                    OutputText = 19,
                    OutputToolCalls = 20,
                    OutputReasoning = 21,
                    InputEstimatedCalls = 22,
                    OutputEstimatedCalls = 23,
                },
            }),
        ]);

        /// <summary>A distinctive context reading, so the unchanged ctx path is observable too.</summary>
        internal int ContextUsagePercent { get; set; } = 37;

        /// <summary>Whether the FIRST prompt (and only the first) parks until released.</summary>
        internal bool ParkFirstPrompt { get; set; }

        private readonly TaskCompletionSource _firstPromptEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstPrompt =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _promptCount;

        /// <summary>Completes once the parked first prompt has ENTERED.</summary>
        internal Task FirstPromptEntered => _firstPromptEntered.Task;

        /// <summary>Releases the parked first prompt (idempotent).</summary>
        internal void ReleaseFirstPrompt() => _releaseFirstPrompt.TrySetResult();

        // ── The usage boundary ───────────────────────────────────────────────

        public void ResetTaskUsage()
        {
            var observed = TaskIdProbe?.Invoke();
            lock (_gate) _resetObservedTaskIds.Add(observed);
        }

        public SharpUsageSummary? GetTaskUsage()
        {
            Interlocked.Increment(ref _getUsageCount);
            var usage = UsageProvider!();
            OnGetTaskUsage?.Invoke();
            return usage;
        }

        // ── The remaining IAgentRunner surface: no-ops ───────────────────────

        public TestResultReport? LastTestReport { get; } = new()
        {
            Verdict = CopilotHive.Workers.TaskVerdict.Pass,
            BuildSuccess = true,
            TotalTests = 1,
            PassedTests = 1,
            FailedTests = 0,
            CoveragePercent = 100,
            Issues = [],
            Summary = "ok",
        };

        public WorkerReport? LastWorkerReport => null;

        public void ClearTestReport() { }
        public void ClearWorkerReport() { }
        public void SetToolBridge(IToolCallBridge? bridge) { }
        public void SetCurrentTaskId(string? taskId) { }
        public void SetCurrentGoalId(string? goalId) { }
        public void SetTesterReport(string? report) { }
        public void SetCustomAgent(DomainWorkerRole role, string agentsMdContent) { }
        public void SetRole(DomainWorkerRole role) { }
        public void SetPhaseReports(IReadOnlyList<PhaseReport>? reports) { }
        public void SetSession(object? session) { }
        public object? GetSession() => null;
        public void SetMaxContextTokens(int maxTokens) { }
        public int GetContextUsagePercent() => ContextUsagePercent;
        public void SetCompactionModel(string? model) { }
        public void SetCompactionMaxTokens(int? maxTokens) { }
        public void SetSubAgentModels(IReadOnlyList<SubAgentModelDto> models) { }
        public void SetConfigProvisioner(Func<string?, CancellationToken, Task>? provisioner) { }
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ResetSessionAsync(string? model, ReasoningEffort? reasoningEffort, CancellationToken ct = default)
            => Task.CompletedTask;

        public async Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            var isFirst = Interlocked.Increment(ref _promptCount) == 1;
            if (isFirst && ParkFirstPrompt)
            {
                _firstPromptEntered.TrySetResult();
                await _releaseFirstPrompt.Task.WaitAsync(ct);
            }

            return "recorded output";
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Records every heartbeat request that reached the transport and answers with an acknowledged
    /// response. Any other unary call is a loud failure: the heartbeat tick must issue no other RPC.
    /// </summary>
    private sealed class RecordingHeartbeatInvoker : CallInvoker
    {
        private readonly object _gate = new();
        private readonly List<HeartbeatRequest> _heartbeats = [];
        private int _heartbeatCalls;

        internal int HeartbeatCalls => Volatile.Read(ref _heartbeatCalls);

        internal IReadOnlyList<HeartbeatRequest> Heartbeats
        {
            get { lock (_gate) return [.. _heartbeats]; }
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException($"Unexpected blocking call {method.FullName}.");

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            if (method.FullName != "/copilothive.HiveOrchestrator/Heartbeat")
                throw new NotSupportedException($"Unexpected unary call {method.FullName}.");

            Interlocked.Increment(ref _heartbeatCalls);
            lock (_gate) _heartbeats.Add(((HeartbeatRequest)(object)request!).Clone());

            return new AsyncUnaryCall<TResponse>(
                Task.FromResult((TResponse)(object)new HeartbeatResponse { Acknowledged = true }),
                Task.FromResult(new Metadata()),
                () => new Status(StatusCode.OK, string.Empty),
                () => new Metadata(),
                () => { });
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

    /// <summary>
    /// A stream writer that counts <c>WorkerReady</c> writes and lets a test await a specific count
    /// deterministically. Both write overloads are implemented explicitly, including the CANCELLABLE
    /// one the worker actually uses.
    /// </summary>
    private sealed class ReadyCountingRequestStream : IClientStreamWriter<WorkerMessage>
    {
        private readonly object _gate = new();
        private readonly Dictionary<int, TaskCompletionSource> _waiters = [];
        private int _readyCount;

        internal int ReadyCount { get { lock (_gate) return _readyCount; } }

        internal Task WaitForReadyCountAsync(int count)
        {
            lock (_gate)
            {
                if (_readyCount >= count)
                    return Task.CompletedTask;
                if (!_waiters.TryGetValue(count, out var waiter))
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters[count] = waiter;
                }

                return waiter.Task;
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
            if (message.PayloadCase != WorkerMessage.PayloadOneofCase.Ready)
                return;

            lock (_gate)
            {
                _readyCount++;
                if (_waiters.Remove(_readyCount, out var waiter))
                    waiter.TrySetResult();
            }
        }
    }

    // ── Fixture helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// A stream writer that accepts and ignores every write. The heartbeat path never writes on the
    /// duplex stream, it only issues the unary RPC.
    /// </summary>
    private sealed class NoopRequestStream : IClientStreamWriter<WorkerMessage>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(WorkerMessage message) => Task.CompletedTask;

        Task IAsyncStreamWriter<WorkerMessage>.WriteAsync(WorkerMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task CompleteAsync() => Task.CompletedTask;
    }

    private static AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> BuildStream(
        IClientStreamWriter<WorkerMessage> requests, IAsyncStreamReader<OrchestratorMessage> responses) =>
        new(requests, responses,
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ => { },
            null!);

    /// <summary>
    /// Replaces the service's real runner with the double, disposing the replaced one exactly as the
    /// other worker fixtures do.
    /// </summary>
    private static void ReplaceRunner(WorkerService service, IAgentRunner runner)
    {
        var field = typeof(WorkerService).GetField("_agentRunner", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("WorkerService._agentRunner field not found.");
        if (field.GetValue(service) is IAgentRunner existing)
            existing.DisposeAsync().AsTask().GetAwaiter().GetResult();
        field.SetValue(service, runner);
    }

    private static WorkerService BuildService(IAgentRunner runner)
    {
        var service = new WorkerService("http://localhost:9999", LocalWorkerId, ["coder"]);
        ReplaceRunner(service, runner);
        return service;
    }

    /// <summary>
    /// Publishes a connection whose gRPC CLIENT is materialised from the fake invoker, so a heartbeat
    /// tick reaches the fake without any live server.
    /// </summary>
    private static WorkerConnection PublishConnection(
        WorkerService service,
        CallInvoker invoker,
        IClientStreamWriter<WorkerMessage>? writer = null,
        IAsyncStreamReader<OrchestratorMessage>? reader = null)
    {
        var stream = BuildStream(writer ?? new NoopRequestStream(), reader ?? new ChannelResponseReader());
        var connection = new WorkerConnection(
            AssignedWorkerId,
            new HiveOrchestrator.HiveOrchestratorClient(invoker),
            stream,
            provisionerOverride: null,
            includeProductionProvisioner: false);
        service.PublishConnection(connection);
        return connection;
    }

    /// <summary>Asserts every count of the runner's reported usage, exactly.</summary>
    private static void AssertHeartbeatUsageMatchesTheRunnerSnapshot(TaskUsage? liveUsage)
    {
        Assert.NotNull(liveUsage);
        var entry = Assert.Single(liveUsage!.Entries);
        Assert.Equal(UsageSourceKind.Agent, entry.Source);
        Assert.True(entry.HasModel, "The runner reported a model, so the wire entry must carry it.");
        Assert.Equal("model-a", entry.Model);
        Assert.Equal(4_321, entry.InputTokens);
        Assert.Equal(321, entry.OutputTokens);
        Assert.Equal(21, entry.CachedInputTokens);
        Assert.Equal(3, entry.ReasoningTokens);
        Assert.Equal(2, entry.Calls);
        Assert.Equal(2, entry.CachedInputReportedCalls);
        Assert.Equal(2, entry.ReasoningReportedCalls);

        // The estimated breakdown is part of the same snapshot, not a separate report.
        Assert.NotNull(entry.Estimated);
        Assert.Equal(11, entry.Estimated.SystemPrompt);
        Assert.Equal(12, entry.Estimated.ToolDefinitions);
        Assert.Equal(13, entry.Estimated.UserText);
        Assert.Equal(14, entry.Estimated.AssistantText);
        Assert.Equal(15, entry.Estimated.ToolCalls);
        Assert.Equal(16, entry.Estimated.ToolResults);
        Assert.Equal(17, entry.Estimated.Reasoning);
        Assert.Equal(18, entry.Estimated.Images);
        Assert.Equal(19, entry.Estimated.OutputText);
        Assert.Equal(20, entry.Estimated.OutputToolCalls);
        Assert.Equal(21, entry.Estimated.OutputReasoning);
        Assert.Equal(22, entry.Estimated.InputEstimatedCalls);
        Assert.Equal(23, entry.Estimated.OutputEstimatedCalls);
    }

    // ── Busy with a usage-reporting runner ───────────────────────────────────

    /// <summary>
    /// A BUSY worker whose runner reports usage sends that snapshot on the heartbeat, tagged with the
    /// task id the same request names, with every count and the estimated breakdown intact. The rest
    /// of the heartbeat (busy, task id, role, ctx) is exactly as before.
    /// </summary>
    [Fact]
    public async Task Heartbeat_BusyWithUsageReportingRunner_CarriesTheSnapshot()
    {
        var runner = new RecordingUsageRunner();
        using var service = BuildService(runner);
        var invoker = new RecordingHeartbeatInvoker();
        var connection = PublishConnection(service, invoker);
        runner.TaskIdProbe = () => service.HeartbeatTaskIdForTest;
        service.SetCurrentTaskIdForTest("task-1");
        SetCurrentRole(service, "coder");

        await service.SendHeartbeatOnceForTestAsync(connection, TestContext.Current.CancellationToken);

        var heartbeat = Assert.Single(invoker.Heartbeats);
        Assert.Equal(AssignedWorkerId, heartbeat.WorkerId);
        Assert.True(heartbeat.Busy);
        Assert.Equal("task-1", heartbeat.CurrentTaskId);
        Assert.Equal("coder", heartbeat.CurrentRole);
        // The pre-existing context path is untouched.
        Assert.Equal(37, heartbeat.ContextUsagePercent);
        AssertHeartbeatUsageMatchesTheRunnerSnapshot(heartbeat.LiveUsage);
        Assert.Equal(1, runner.GetUsageCount);
    }

    /// <summary>
    /// THE LATEST SNAPSHOT WINS: a second tick reports the runner's CURRENT snapshot, never a cached
    /// or accumulated one.
    /// </summary>
    [Fact]
    public async Task Heartbeat_SecondTick_ReportsTheLatestSnapshot()
    {
        var runner = new RecordingUsageRunner();
        using var service = BuildService(runner);
        var invoker = new RecordingHeartbeatInvoker();
        var connection = PublishConnection(service, invoker);
        service.SetCurrentTaskIdForTest("task-1");
        await service.SendHeartbeatOnceForTestAsync(connection, TestContext.Current.CancellationToken);
        AssertHeartbeatUsageMatchesTheRunnerSnapshot(invoker.Heartbeats[0].LiveUsage);

        runner.UsageProvider = () => new SharpUsageSummary(
        [
            new SharpUsageEntry(SharpUsageSource.Compaction, "model-b", new SharpTokenUsage
            {
                InputTokens = 7,
                OutputTokens = 8,
                Calls = 1,
                CachedInputReportedCalls = 0,
                ReasoningReportedCalls = 1,
            }),
        ]);

        await service.SendHeartbeatOnceForTestAsync(connection, TestContext.Current.CancellationToken);

        Assert.Equal(2, invoker.HeartbeatCalls);
        var second = Assert.Single(invoker.Heartbeats[1].LiveUsage!.Entries);
        Assert.Equal(UsageSourceKind.Compaction, second.Source);
        Assert.Equal("model-b", second.Model);
        Assert.Equal(7, second.InputTokens);
        Assert.Equal(8, second.OutputTokens);
        Assert.Equal(1, second.Calls);
        Assert.Equal(0, second.CachedInputReportedCalls);
    }

    // ── Idle / runner without usage ──────────────────────────────────────────

    /// <summary>
    /// AN IDLE WORKER SENDS NO LIVE USAGE — and never even asks the runner for one, so an idle tick
    /// cannot report a stale or fabricated snapshot. A PRESENT-but-empty summary is deliberately NOT
    /// what an idle worker sends: the field stays ABSENT ("no live usage reported").
    /// </summary>
    [Fact]
    public async Task Heartbeat_Idle_SendsNoLiveUsageAndNeverAsksTheRunner()
    {
        var runner = new RecordingUsageRunner();
        using var service = BuildService(runner);
        var invoker = new RecordingHeartbeatInvoker();
        var connection = PublishConnection(service, invoker);

        await service.SendHeartbeatOnceForTestAsync(connection, TestContext.Current.CancellationToken);

        var heartbeat = Assert.Single(invoker.Heartbeats);
        Assert.False(heartbeat.Busy);
        Assert.Equal(string.Empty, heartbeat.CurrentTaskId);
        Assert.Equal(0, heartbeat.ContextUsagePercent);
        Assert.Null(heartbeat.LiveUsage);
        Assert.Equal(0, runner.GetUsageCount);
    }

    /// <summary>
    /// A BUSY WORKER WHOSE RUNNER DOES NOT REPORT USAGE sends no live usage: a <c>null</c> snapshot
    /// is ABSENCE, never an empty (zero) measurement.
    /// </summary>
    [Fact]
    public async Task Heartbeat_BusyWithNullUsage_SendsNoLiveUsage()
    {
        var runner = new RecordingUsageRunner { UsageProvider = () => null };
        using var service = BuildService(runner);
        var invoker = new RecordingHeartbeatInvoker();
        var connection = PublishConnection(service, invoker);
        service.SetCurrentTaskIdForTest("task-1");

        await service.SendHeartbeatOnceForTestAsync(connection, TestContext.Current.CancellationToken);

        var heartbeat = Assert.Single(invoker.Heartbeats);
        Assert.True(heartbeat.Busy);
        Assert.Equal("task-1", heartbeat.CurrentTaskId);
        Assert.Null(heartbeat.LiveUsage);
        Assert.Equal(1, runner.GetUsageCount);
    }

    // ── Transition consistency ───────────────────────────────────────────────

    /// <summary>
    /// THE TRANSITION-CONSISTENCY CHECK. The runner's <see cref="IAgentRunner.GetTaskUsage"/> moves
    /// the current task id from A to B through the production field BEFORE returning B's usage — the
    /// exact interleaving an assignment transition produces. The tick then carries NO live usage: B's
    /// snapshot is never tagged with A's id.
    /// </summary>
    /// <remarks>
    /// THE CONTROL IS THE SAME FAKE WITHOUT THE SWITCH: with no transition the identical runner DOES
    /// carry its snapshot, so the absence above is caused by the id change and nothing else. The two
    /// cells share every other input.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Heartbeat_TaskIdChangedDuringTheUsageRead_LeavesLiveUsageAbsent(bool switchTaskIdDuringRead)
    {
        var runner = new RecordingUsageRunner();
        using var service = BuildService(runner);
        var invoker = new RecordingHeartbeatInvoker();
        var connection = PublishConnection(service, invoker);
        service.SetCurrentTaskIdForTest("task-A");
        runner.OnGetTaskUsage = switchTaskIdDuringRead
            ? () => service.SetCurrentTaskIdForTest("task-B")
            : null;

        await service.SendHeartbeatOnceForTestAsync(connection, TestContext.Current.CancellationToken);

        var heartbeat = Assert.Single(invoker.Heartbeats);
        // The request still names the task the tick CAPTURED, in both cells.
        Assert.True(heartbeat.Busy);
        Assert.Equal("task-A", heartbeat.CurrentTaskId);

        if (switchTaskIdDuringRead)
        {
            Assert.Null(heartbeat.LiveUsage);
            Assert.Equal(1, runner.GetUsageCount);
            Assert.Equal("task-B", service.HeartbeatTaskIdForTest);
        }
        else
        {
            AssertHeartbeatUsageMatchesTheRunnerSnapshot(heartbeat.LiveUsage);
        }
    }

    /// <summary>
    /// A task id that is cleared during the usage read (the assignment finished) is the same
    /// transition, and it also leaves the usage ABSENT rather than tagging it with the finished task.
    /// </summary>
    [Fact]
    public async Task Heartbeat_TaskIdClearedDuringTheUsageRead_LeavesLiveUsageAbsent()
    {
        var runner = new RecordingUsageRunner();
        using var service = BuildService(runner);
        var invoker = new RecordingHeartbeatInvoker();
        var connection = PublishConnection(service, invoker);
        service.SetCurrentTaskIdForTest("task-A");
        runner.OnGetTaskUsage = () => service.SetCurrentTaskIdForTest(null);

        await service.SendHeartbeatOnceForTestAsync(connection, TestContext.Current.CancellationToken);

        var heartbeat = Assert.Single(invoker.Heartbeats);
        Assert.Equal("task-A", heartbeat.CurrentTaskId);
        Assert.Null(heartbeat.LiveUsage);
    }

    // ── The swallow-and-retry fault contract ────────────────────────────────

    /// <summary>
    /// A FAILURE WHILE READING THE USAGE IS SWALLOWED LIKE ANY OTHER HEARTBEAT FAULT. The runner
    /// throws from <c>GetTaskUsage</c>; the tick completes normally, writes the sanitized diagnostic
    /// on stderr, and issues no RPC at all.
    /// </summary>
    /// <remarks>
    /// REMOVAL PROOF. Without the tick's existing catch this throw escapes
    /// <see cref="WorkerService.SendHeartbeatOnceForTestAsync"/>, so the awaited tick faults and this
    /// test fails by name — which is exactly how the production heartbeat loop would stop
    /// heartbeating.
    /// </remarks>
    [Fact]
    public async Task Heartbeat_UsageReadThrows_IsSwallowedWithASanitizedDiagnostic()
    {
        var runner = new RecordingUsageRunner
        {
            UsageProvider = () => throw new InvalidOperationException("usage read failed"),
        };
        using var service = BuildService(runner);
        var invoker = new RecordingHeartbeatInvoker();
        var connection = PublishConnection(service, invoker);
        service.SetCurrentTaskIdForTest("task-1");

        var originalErr = Console.Error;
        var capturedErr = new StringWriter();
        var tick = Task.CompletedTask;
        try
        {
            Console.SetError(capturedErr);
            tick = service.SendHeartbeatOnceForTestAsync(connection, TestContext.Current.CancellationToken);
            await tick.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetError(originalErr);
            await tick.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }

        Assert.True(tick.IsCompletedSuccessfully, "A usage-read fault must be swallowed, never propagated.");
        Assert.Equal(0, invoker.HeartbeatCalls);
        var diagnostics = capturedErr.ToString();
        Assert.Contains("Heartbeat failed", diagnostics, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("usage read failed", diagnostics, StringComparison.Ordinal);
    }

    /// <summary>
    /// A MAPPING FAILURE IS SWALLOWED TOO: a snapshot whose source has no wire equivalent cannot be
    /// put on the wire, so the tick drops the whole request for that tick — the fault is not
    /// propagated.
    /// </summary>
    [Fact]
    public async Task Heartbeat_UnmappableUsage_IsSwallowedAndSendsNoRpc()
    {
        var runner = new RecordingUsageRunner
        {
            UsageProvider = () => new SharpUsageSummary(
            [
                new SharpUsageEntry(
                    (SharpUsageSource)99,
                    "model-x",
                    new SharpTokenUsage { InputTokens = 5, Calls = 1 }),
            ]),
        };
        using var service = BuildService(runner);
        var invoker = new RecordingHeartbeatInvoker();
        var connection = PublishConnection(service, invoker);
        service.SetCurrentTaskIdForTest("task-1");

        var originalErr = Console.Error;
        var capturedErr = new StringWriter();
        var tick = Task.CompletedTask;
        try
        {
            Console.SetError(capturedErr);
            tick = service.SendHeartbeatOnceForTestAsync(connection, TestContext.Current.CancellationToken);
            await tick.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetError(originalErr);
            await tick.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }

        Assert.True(tick.IsCompletedSuccessfully, "An unmappable usage must be swallowed, never propagated.");
        Assert.Equal(0, invoker.HeartbeatCalls);
        Assert.Contains("Heartbeat failed", capturedErr.ToString(), StringComparison.Ordinal);
    }

    // ── The assignment path: reset BEFORE the task id is published ───────────

    /// <summary>
    /// THE RESET-BEFORE-PUBLISH ORDERING, observed through the REAL message loop. The recording
    /// runner reads the very field the heartbeat reads at each of its <c>ResetTaskUsage</c> calls, so
    /// the FIRST recorded observation proves this assignment's id was NOT yet published when the
    /// reset ran — and a later observation of the same field proves publication did happen.
    /// </summary>
    /// <remarks>
    /// REMOVAL PROOF. If the reset were moved after the publication (or dropped from the assignment
    /// site entirely), the FIRST reset observed would already carry this assignment's id — or would
    /// only ever be the executor's, which also runs after publication — and the first assertion fails
    /// by name. A heartbeat can therefore never attach the previous task's usage to the new id.
    /// </remarks>
    [Fact]
    public async Task AssignmentPath_ResetsUsageBeforeTheTaskIdIsPublished()
    {
        var runner = new RecordingUsageRunner();
        using var service = BuildService(runner);
        runner.TaskIdProbe = () => service.HeartbeatTaskIdForTest;

        var responses = new ChannelResponseReader();
        var requests = new ReadyCountingRequestStream();
        var connection = TestConnectionFactory.Attach(service, AssignedWorkerId, BuildStream(requests, responses));
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);
        var cleanedUp = false;
        var primaryInFlight = false;

        try
        {
            responses.Push(Assignment("task-A"));
            await requests.WaitForReadyCountAsync(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var observed = runner.ResetObservedTaskIds;
            Assert.NotEmpty(observed);
            Assert.Null(observed[0]);
            // Non-vacuity: the SAME seam that observed the null above observes this assignment's id
            // at a LATER reset (the executor's own, which runs after publication), so the null really
            // means "not published yet" rather than "never published".
            Assert.Contains("task-A", observed);
            Assert.Equal("task-A", observed[^1]);

            responses.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            await FinalCleanupAsync(service, runner, responses, loop, primaryInFlight: false);
            cleanedUp = true;
        }
        catch
        {
            primaryInFlight = true;
            throw;
        }
        finally
        {
            // THE EARLY-FAILURE PATH: an assertion above failed before the normal-path cleanup ran.
            // The same deterministic cleanup still releases, joins and drains every owned task BEFORE
            // the service is disposed, without replacing the propagating failure.
            if (!cleanedUp)
                await FinalCleanupAsync(service, runner, responses, loop, primaryInFlight);
        }
    }

    /// <summary>
    /// THE ASSIGNMENT PATH END TO END: while the loop-assigned task is running, a heartbeat carries
    /// exactly that task's runner snapshot, tagged with the task id the request names — and, with the
    /// body parked, the id is the one the ASSIGNMENT published, not a fixture-supplied value.
    /// </summary>
    [Fact]
    public async Task AssignmentPath_HeartbeatCarriesTheAssignedTasksLiveUsage()
    {
        var runner = new RecordingUsageRunner { ParkFirstPrompt = true };
        using var service = BuildService(runner);
        var invoker = new RecordingHeartbeatInvoker();
        var responses = new ChannelResponseReader();
        var requests = new ReadyCountingRequestStream();
        var connection = PublishConnection(service, invoker, requests, responses);
        var loop = InvokeProcessMessages(service, connection, TestContext.Current.CancellationToken);
        var cleanedUp = false;
        var primaryInFlight = false;

        try
        {
            responses.Push(Assignment("task-live"));
            await runner.FirstPromptEntered.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Same(connection, PublishedConnection(service));
            // The body is parked inside the prompt: the worker is busy on the assigned task and its
            // runner reports usage for it.
            Assert.Equal("task-live", service.HeartbeatTaskIdForTest);

            await service.SendHeartbeatOnceForTestAsync(connection, TestContext.Current.CancellationToken);

            var heartbeat = Assert.Single(invoker.Heartbeats);
            Assert.True(heartbeat.Busy);
            Assert.Equal("task-live", heartbeat.CurrentTaskId);
            AssertHeartbeatUsageMatchesTheRunnerSnapshot(heartbeat.LiveUsage);

            // A tick on the SAME live assignment replaces nothing and keeps reporting it.
            await service.SendHeartbeatOnceForTestAsync(connection, TestContext.Current.CancellationToken);
            Assert.Equal(2, invoker.HeartbeatCalls);
            Assert.Equal("task-live", invoker.Heartbeats[1].CurrentTaskId);
            Assert.NotNull(invoker.Heartbeats[1].LiveUsage);

            // DETERMINISTIC END OF THE STREAM: EOF arrives while the body is STILL PARKED in its
            // prompt (nothing has released it), so no ordinary Ready can have started and the live-
            // token stream loss CARRIES the assignment — it is NOT drained by the loop. That is
            // exactly the shape that, without the process-level drain below, would leave execution,
            // reporting and the carried delivery running past the test.
            responses.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var carried = GetActiveAssignment(service);
            Assert.NotNull(carried);
            Assert.Equal(CarriedState, GetAssignmentStateOf(carried));
            Assert.False(carried.GetType().GetProperty("Execution")!.GetValue(carried) is Task { IsCompleted: true },
                "The carried execution is still parked in its prompt before the final drain.");

            await FinalCleanupAsync(service, runner, responses, loop, primaryInFlight: false);
            cleanedUp = true;
        }
        catch
        {
            primaryInFlight = true;
            throw;
        }
        finally
        {
            // THE EARLY-FAILURE PATH: the same deterministic cleanup runs BEFORE the service's
            // disposal, never replacing the propagating assertion failure.
            if (!cleanedUp)
                await FinalCleanupAsync(service, runner, responses, loop, primaryInFlight);
        }
    }

    // ── Deterministic final cleanup of owned assignment work ─────────────────

    /// <summary>The production value of the assignment state cell's <c>Carried</c> state.</summary>
    private const int CarriedState = 2;

    /// <summary>The service's ownership slot (observation only), or <c>null</c> when empty.</summary>
    private static object? GetActiveAssignment(WorkerService service) =>
        typeof(WorkerService)
            .GetField("_activeAssignment", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service);

    /// <summary>The numeric value of an assignment's one atomic state cell.</summary>
    private static int GetAssignmentStateOf(object assignment)
    {
        var state = assignment.GetType().GetProperty("State")!.GetValue(assignment)!;
        return (int)state.GetType().GetProperty("Value")!.GetValue(state)!;
    }

    /// <summary>
    /// EVERY task the given retained assignment owns — execution, reporting, the gated
    /// retransmission, the retained ordinary readiness write and the carried delivery — each named,
    /// <c>null</c> entries omitted. Captured BEFORE the drain clears the slot, so the joins below can
    /// prove the drain really completed each one.
    /// </summary>
    private static List<(string Name, Task Task)> CaptureOwnedTasks(object assignment)
    {
        var type = assignment.GetType();
        var slot = type.GetProperty("OrdinaryReady")!.GetValue(assignment)!;
        var owned = new (string Name, Task? Task)[]
        {
            ("execution", (Task?)type.GetProperty("Execution")!.GetValue(assignment)),
            ("reporting", (Task?)type.GetProperty("Reporting")!.GetValue(assignment)),
            ("retransmission", (Task?)type.GetProperty("Retry")!.GetValue(assignment)),
            ("readiness write", (Task?)slot.GetType().GetProperty("Write")!.GetValue(slot)),
            ("carried delivery", (Task?)type.GetProperty("CarriedDelivery")!.GetValue(assignment)),
        };

        return [.. owned.Where(o => o.Task is not null).Select(o => (o.Name, o.Task!))];
    }

    /// <summary>
    /// THE FIXTURE'S DETERMINISTIC FINAL CLEANUP, run BEFORE the service is disposed on BOTH the
    /// normal and the early-failure path. It never relies on EOF winning a race against the ordinary
    /// Ready, nor on the test token: an EOF on a live token may CARRY the assignment instead of
    /// draining it, and a carried delivery waits for an adoption these fixtures never provide.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ORDER: end the reader and JOIN the loop; capture every task the (possibly carried) retained
    /// assignment owns; run the process's own final cleanup,
    /// <see cref="WorkerService.DrainCarriedAssignmentAsync"/> — the same drain production and the
    /// ownership fixtures use precisely because EOF may carry rather than drain — which cancels the
    /// assignment and joins all its owned work; then release the parked prompt as a no-op safety net
    /// and ASSERT the outcome explicitly: the ownership slot is empty, the heartbeat task state is
    /// cleared, and every captured owned task is terminal.
    /// </para>
    /// <para>
    /// WITH A PRIMARY FAILURE IN FLIGHT every step is still attempted, but a cleanup failure is
    /// swallowed so it can never replace the propagating assertion failure.
    /// </para>
    /// </remarks>
    private static async Task FinalCleanupAsync(
        WorkerService service,
        RecordingUsageRunner runner,
        ChannelResponseReader responses,
        Task loop,
        bool primaryInFlight)
    {
        try
        {
            responses.TryComplete();
            try
            {
                await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            }
            catch (Exception) when (primaryInFlight)
            {
                // The loop's outcome is asserted on the normal path; here only quiescence matters.
            }

            var retained = GetActiveAssignment(service);
            var owned = retained is null ? [] : CaptureOwnedTasks(retained);

            await service.DrainCarriedAssignmentAsync().WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.ReleaseFirstPrompt();

            foreach (var (name, task) in owned)
            {
                // The drain JOINED these; a bounded wait here only turns a regression into a named
                // failure instead of a hang. The task's own outcome (a cancellation) is irrelevant.
                try { await task.WaitAsync(Failsafe, TestContext.Current.CancellationToken); }
                catch (TimeoutException) { throw new Xunit.Sdk.XunitException($"Owned {name} task was not joined by the final drain."); }
                catch (Exception) { }

                Assert.True(task.IsCompleted, $"Owned {name} task must be terminal after the final drain.");
            }

            Assert.True(loop.IsCompleted, "The message loop must be joined before disposal.");
            Assert.Null(GetActiveAssignment(service));
            Assert.Null(service.HeartbeatTaskIdForTest);
        }
        catch (Exception) when (primaryInFlight)
        {
            // Never replace the propagating primary failure.
            runner.ReleaseFirstPrompt();
        }
    }

    /// <summary>
    /// A heartbeat tick is issued on the PUBLISHED connection of the registration, never on a
    /// separately mutable identity value.
    /// </summary>
    private static WorkerConnection? PublishedConnection(WorkerService service) =>
        (WorkerConnection?)typeof(WorkerService)
            .GetField("_connection", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service);

    /// <summary>
    /// Writes the same field the heartbeat reads for the current role, so the unchanged role part of
    /// the heartbeat is observable without adding a production seam for it.
    /// </summary>
    private static void SetCurrentRole(WorkerService service, string? role) =>
        typeof(WorkerService).GetField("_currentRole", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, role);

    private static Task InvokeProcessMessages(WorkerService service, WorkerConnection connection, CancellationToken ct) =>
        (Task)typeof(WorkerService)
            .GetMethod("ProcessMessagesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [connection, ct])!;

    private static OrchestratorMessage Assignment(string taskId) => new()
    {
        Assignment = new TaskAssignment
        {
            TaskId = taskId,
            GoalId = "goal-live-usage",
            GoalDescription = "live usage",
            Prompt = "do the work",
            Role = Shared.Grpc.WorkerRole.Coder,
            Model = "model-a",
        },
    };
}
