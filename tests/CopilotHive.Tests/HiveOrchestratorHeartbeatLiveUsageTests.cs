using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;

using Google.Protobuf;

using Grpc.Core;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using GrpcUsageSourceKind = CopilotHive.Shared.Grpc.UsageSourceKind;
using SharpUsageEntry = SharpCoder.UsageEntry;
using SharpUsageSource = SharpCoder.UsageSource;
using SharpTokenUsage = SharpCoder.TokenUsage;

namespace CopilotHive.Tests;

/// <summary>
/// The <see cref="HiveOrchestratorService.Heartbeat"/> live-usage contract: a heartbeat that carries
/// a usable usage snapshot stores it TOGETHER with the task id it belongs to, and EVERY heartbeat
/// that does not — no usage, no task id, or an unmappable usage — clears the pair while still being
/// applied and acknowledged.
/// </summary>
/// <remarks>
/// The requests are delivered through the REAL RPC entry point onto a REAL <see cref="WorkerPool"/>,
/// so the assertions observe what a worker's heartbeat actually leaves behind. No wall-clock timing
/// is involved: nothing here sleeps or waits, and the heartbeat path is called directly.
/// </remarks>
public sealed class HiveOrchestratorHeartbeatLiveUsageTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ServerCallContext MockContext() => new Mock<ServerCallContext>().Object;

    /// <summary>
    /// Builds a service over a real pool with a CAPTURING logger, so a mapping failure can be asserted
    /// to have been logged instead of silently dropped.
    /// </summary>
    private static (HiveOrchestratorService service, WorkerPool pool, TestLogger<HiveOrchestratorService> logger)
        CreateService()
    {
        var pool = new WorkerPool();
        var taskQueue = new TaskQueue();
        var pipelineManager = new GoalPipelineManager();
        var completionNotifier = new TaskCompletionNotifier();
        var goalManager = new GoalManager();
        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            taskQueue,
            new GrpcWorkerGateway(pool),
            completionNotifier,
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

        var logger = new TestLogger<HiveOrchestratorService>();
        var service = new HiveOrchestratorService(
            pool,
            taskQueue,
            pipelineManager,
            completionNotifier,
            dispatcher,
            logger);

        return (service, pool, logger);
    }

    /// <summary>Builds a heartbeat with DISTINCT non-zero counts in its live usage.</summary>
    private static HeartbeatRequest HeartbeatWithUsage(string workerId, string taskId, int ctx = 42, long seed = 1) =>
        new()
        {
            WorkerId = workerId,
            Busy = true,
            CurrentTaskId = taskId,
            ContextUsagePercent = ctx,
            LiveUsage = new TaskUsage
            {
                Entries =
                {
                    new TaskUsageEntry
                    {
                        Source = GrpcUsageSourceKind.Agent,
                        Model = "model-a",
                        InputTokens = 100 + seed,
                        OutputTokens = 200 + seed,
                        CachedInputTokens = 300 + seed,
                        ReasoningTokens = 400 + seed,
                        Calls = 1,
                        CachedInputReportedCalls = 1,
                        ReasoningReportedCalls = 1,
                    },
                },
            },
        };

    /// <summary>Asserts the pair, or its absence, on the registered worker.</summary>
    private static void AssertStoredPair(ConnectedWorker worker, string? expectedTaskId, long? expectedSeed)
    {
        if (expectedTaskId is null)
        {
            Assert.Null(worker.LiveUsageTaskId);
            Assert.Null(worker.LiveUsage);
            return;
        }

        Assert.Equal(expectedTaskId, worker.LiveUsageTaskId);
        Assert.NotNull(worker.LiveUsage);
        var entry = Assert.Single(worker.LiveUsage!.Entries);
        Assert.Equal(SharpUsageSource.Agent, entry.Source);
        Assert.Equal("model-a", entry.Model);
        Assert.Equal(100 + expectedSeed!.Value, entry.Usage.InputTokens);
        Assert.Equal(200 + expectedSeed.Value, entry.Usage.OutputTokens);
        Assert.Equal(300 + expectedSeed.Value, entry.Usage.CachedInputTokens);
        Assert.Equal(400 + expectedSeed.Value, entry.Usage.ReasoningTokens);
        Assert.Equal(1, entry.Usage.Calls);
    }

    // ── Valid live usage ─────────────────────────────────────────────────────

    /// <summary>
    /// A heartbeat carrying live usage AND a non-empty task id stores BOTH, with the usage mapped
    /// through the domain mapper — the worker's own current task and the stored tag agree, and the
    /// mapped counts are exactly the wire counts.
    /// </summary>
    [Fact]
    public async Task Heartbeat_WithLiveUsageAndTaskId_StoresBoth()
    {
        var (service, pool, _) = CreateService();
        var worker = pool.RegisterWorker("w1", []);
        worker.IsBusy = true;
        worker.CurrentTaskId = "task-1";

        var response = await service.Heartbeat(HeartbeatWithUsage("w1", "task-1"), MockContext());

        Assert.True(response.Acknowledged);
        AssertStoredPair(worker, "task-1", 1);
        // The heartbeat itself was applied as usual.
        Assert.Equal(42, worker.ContextUsagePercent);
    }

    /// <summary>
    /// A SUCCESSIVE valid heartbeat REPLACES the stored pair — the latest snapshot and its task id
    /// win, and no accumulation happens across heartbeats.
    /// </summary>
    [Fact]
    public async Task Heartbeat_SecondValidUsage_ReplacesTheStoredPair()
    {
        var (service, pool, _) = CreateService();
        var worker = pool.RegisterWorker("w1", []);
        await service.Heartbeat(HeartbeatWithUsage("w1", "task-1", seed: 1), MockContext());
        AssertStoredPair(worker, "task-1", 1);

        await service.Heartbeat(HeartbeatWithUsage("w1", "task-2", ctx: 43, seed: 2), MockContext());

        AssertStoredPair(worker, "task-2", 2);
        Assert.Equal(43, worker.ContextUsagePercent);
    }

    // ── No usage reported ────────────────────────────────────────────────────

    /// <summary>
    /// A HEARTBEAT WITHOUT USAGE CLEARS A PREVIOUSLY STORED PAIR: the worker stopped reporting live
    /// usage, so neither the snapshot nor its task id may outlive the heartbeat that omitted it — and
    /// the heartbeat is still applied and acknowledged.
    /// </summary>
    [Fact]
    public async Task Heartbeat_WithoutUsage_ClearsPreviouslyStoredPair()
    {
        var (service, pool, _) = CreateService();
        var worker = pool.RegisterWorker("w1", []);
        await service.Heartbeat(HeartbeatWithUsage("w1", "task-1"), MockContext());
        AssertStoredPair(worker, "task-1", 1);

        var response = await service.Heartbeat(
            new HeartbeatRequest { WorkerId = "w1", Busy = true, CurrentTaskId = "task-1", ContextUsagePercent = 44 },
            MockContext());

        Assert.True(response.Acknowledged);
        AssertStoredPair(worker, expectedTaskId: null, expectedSeed: null);
        // The heartbeat was still applied, exactly as for a usage-carrying one.
        Assert.Equal(44, worker.ContextUsagePercent);
    }

    /// <summary>
    /// A heartbeat whose usage carries an EMPTY task id clears the pair too: the usage cannot be
    /// attributed, so it must never be stored under the previous task's id.
    /// </summary>
    [Fact]
    public async Task Heartbeat_EmptyTaskIdWithUsage_ClearsBoth()
    {
        var (service, pool, _) = CreateService();
        var worker = pool.RegisterWorker("w1", []);
        await service.Heartbeat(HeartbeatWithUsage("w1", "task-1"), MockContext());
        AssertStoredPair(worker, "task-1", 1);

        var response = await service.Heartbeat(HeartbeatWithUsage("w1", taskId: "", ctx: 45, seed: 2), MockContext());

        Assert.True(response.Acknowledged);
        AssertStoredPair(worker, expectedTaskId: null, expectedSeed: null);
        Assert.Equal(45, worker.ContextUsagePercent);
    }

    // ── Malformed usage ──────────────────────────────────────────────────────

    /// <summary>
    /// A MALFORMED live usage (an unknown <see cref="GrpcUsageSourceKind"/>) clears both stored values
    /// — it is never partially applied — while the heartbeat is STILL APPLIED (timestamp and context
    /// usage) and ACKNOWLEDGED, so a worker with a broken usage report is not made to look dead.
    /// The failure is logged.
    /// </summary>
    [Fact]
    public async Task Heartbeat_MalformedUsage_ClearsBothAndIsStillAppliedAndAcknowledged()
    {
        var (service, pool, logger) = CreateService();
        var worker = pool.RegisterWorker("w1", []);
        await service.Heartbeat(HeartbeatWithUsage("w1", "task-1"), MockContext());
        AssertStoredPair(worker, "task-1", 1);

        // The source is written RAW, bypassing the mapper, exactly as a broken/unknown sender would.
        var malformed = new HeartbeatRequest
        {
            WorkerId = "w1",
            Busy = true,
            CurrentTaskId = "task-1",
            ContextUsagePercent = 46,
            LiveUsage = new TaskUsage
            {
                Entries = { new TaskUsageEntry { Source = (GrpcUsageSourceKind)5, InputTokens = 7, Calls = 1 } },
            },
        };

        var response = await service.Heartbeat(malformed, MockContext());

        Assert.True(response.Acknowledged);
        AssertStoredPair(worker, expectedTaskId: null, expectedSeed: null);
        // The malformed usage did NOT stop the heartbeat from being applied.
        Assert.Equal(46, worker.ContextUsagePercent);

        Assert.Contains(
            logger.LogEntries,
            e => e.LogLevel == LogLevel.Warning
                 && e.Message.Contains("w1", StringComparison.Ordinal)
                 && e.Message.Contains("task-1", StringComparison.Ordinal));
    }

    /// <summary>
    /// A heartbeat for an UNKNOWN worker carrying usage is a no-op that still acknowledges: nothing is
    /// created in the pool and no entry appears from the request.
    /// </summary>
    [Fact]
    public async Task Heartbeat_UnknownWorkerWithUsage_IsNoOpAndAcknowledged()
    {
        var (service, pool, _) = CreateService();

        var response = await service.Heartbeat(HeartbeatWithUsage("ghost", "task-1"), MockContext());

        Assert.True(response.Acknowledged);
        Assert.Null(pool.GetWorker("ghost"));
        Assert.Empty(pool.CaptureWorkerStatus());
    }

    // ── Round-trip of the wire contract ──────────────────────────────────────

    /// <summary>
    /// The live usage survives the WIRE: a request serialized and parsed again still stores the same
    /// snapshot with its task id, so the new proto field really carries the payload.
    /// </summary>
    [Fact]
    public async Task Heartbeat_LiveUsageSurvivesTheWire_AndIsStored()
    {
        var (service, pool, _) = CreateService();
        var worker = pool.RegisterWorker("w1", []);

        var decoded = HeartbeatRequest.Parser.ParseFrom(HeartbeatWithUsage("w1", "task-wire", seed: 3).ToByteArray());

        var response = await service.Heartbeat(decoded, MockContext());

        Assert.True(response.Acknowledged);
        AssertStoredPair(worker, "task-wire", 3);
    }

    /// <summary>
    /// The new field is ADDITIVE: a legacy heartbeat that never touches field 7 decodes with the field
    /// absent, so the sender is treated as reporting no live usage and any previously stored pair is
    /// cleared — never as a zero measurement.
    /// </summary>
    [Fact]
    public async Task Heartbeat_LegacyMessageWithoutField7_ClearsThePair()
    {
        var (service, pool, _) = CreateService();
        var worker = pool.RegisterWorker("w1", []);
        await service.Heartbeat(HeartbeatWithUsage("w1", "task-1"), MockContext());
        AssertStoredPair(worker, "task-1", 1);

        var legacy = new HeartbeatRequest { WorkerId = "w1", Busy = false, ContextUsagePercent = 47 };
        var encoded = legacy.ToByteArray();
        var decoded = HeartbeatRequest.Parser.ParseFrom(encoded);
        Assert.Null(decoded.LiveUsage);

        var response = await service.Heartbeat(decoded, MockContext());

        Assert.True(response.Acknowledged);
        AssertStoredPair(worker, expectedTaskId: null, expectedSeed: null);
        Assert.Equal(47, worker.ContextUsagePercent);
    }
}
