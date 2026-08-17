using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Services;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Verifies that a <see cref="CiMonitorService"/> handed to <see cref="GoalDispatcher"/> is
/// actually wired through to <c>GoalLifecycleService</c> and invoked on goal completion —
/// and, just as importantly, that it is NOT invoked for any excluded state.
/// </summary>
public sealed class CiMonitorWiringTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A <see cref="CiMonitorService"/> subclass that records invocations instead of doing I/O.
    /// Subclassing (rather than mocking) is required because the production type is a concrete
    /// class; the entry points are <c>virtual</c> precisely to support this seam.
    /// </summary>
    private sealed class RecordingCiMonitor : CiMonitorService
    {
        private readonly TaskCompletionSource<bool> _invoked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? GoalId { get; private set; }
        public string? Hashes { get; private set; }
        public List<string>? RepoNames { get; private set; }
        public CancellationToken ObservedToken { get; private set; }
        public int InvocationCount;

        /// <summary>When set, the monitor throws this exception to test caller isolation.</summary>
        public Exception? ThrowOnInvoke { get; init; }

        /// <summary>Completes once <see cref="MonitorGoalAsync"/> has been entered.</summary>
        public Task Invoked => _invoked.Task;

        public override Task MonitorGoalAsync(
            string goalId, string commaSeparatedHashes, List<string> repoNames, CancellationToken ct)
        {
            Interlocked.Increment(ref InvocationCount);
            GoalId = goalId;
            Hashes = commaSeparatedHashes;
            RepoNames = repoNames;
            ObservedToken = ct;
            _invoked.TrySetResult(true);

            if (ThrowOnInvoke is not null)
                throw ThrowOnInvoke;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception), exception));
        }

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Snapshot()
        {
            lock (Entries) return Entries.ToList();
        }
    }

    private static GoalPipeline CreatePipeline(string goalId, params string[] repoNames)
    {
        var goal = new Goal
        {
            Id = goalId,
            Description = "Wiring test goal",
            RepositoryNames = [.. repoNames],
        };
        return new GoalPipeline(goal);
    }

    /// <summary>
    /// Builds a lifecycle service backed by a real in-memory store containing
    /// <paramref name="pipeline"/>'s goal, so status persistence succeeds and execution
    /// reaches the CI-monitoring launch site.
    /// </summary>
    private static GoalLifecycleService CreateLifecycleService(
        CiMonitorService? ciMonitor, GoalPipeline pipeline, ILogger? logger = null)
    {
        var store = new InMemoryGoalStore();
        store.CreateGoalAsync(pipeline.Goal).GetAwaiter().GetResult();
        var manager = new GoalManager();
        manager.AddSource(store);
        return new GoalLifecycleService(
            manager,
            logger ?? NullLogger.Instance,
            ciMonitor: ciMonitor);
    }

    /// <summary>
    /// Waits for a fire-and-forget task that should NOT run. Returns true if it unexpectedly ran.
    /// A short grace period is enough because the launch is synchronous up to the first await.
    /// </summary>
    private static async Task<bool> RanUnexpectedlyAsync(RecordingCiMonitor monitor)
    {
        var completed = await Task.WhenAny(monitor.Invoked, Task.Delay(TimeSpan.FromMilliseconds(250)));
        return completed == monitor.Invoked;
    }

    // ── Positive: completion launches monitoring ───────────────────────────

    [Fact]
    public async Task FinalizeGoalAsync_CompletedWithMergeHash_LaunchesMonitoringWithGoalArguments()
    {
        var monitor = new RecordingCiMonitor();
        var pipeline = CreatePipeline("goal-1", "repo-a", "repo-b");
        var service = CreateLifecycleService(monitor, pipeline);

        await service.FinalizeGoalAsync(
            pipeline, GoalStatus.Completed, failureReason: null,
            mergeCommitHash: "sha-a,sha-b", TestContext.Current.CancellationToken);

        // Fire-and-forget: the monitor runs on a detached task, so wait for the signal.
        await monitor.Invoked.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(1, monitor.InvocationCount);
        Assert.Equal("goal-1", monitor.GoalId);
        Assert.Equal("sha-a,sha-b", monitor.Hashes);
        Assert.Equal(["repo-a", "repo-b"], monitor.RepoNames);
    }

    /// <summary>
    /// The detached task must carry <see cref="CancellationToken.None"/>: CI monitoring
    /// outlives the completion call, and each repo enforces its own CiTimeoutMinutes. If the
    /// caller's token were forwarded, monitoring would be aborted the moment the pipeline
    /// shut down.
    /// </summary>
    [Fact]
    public async Task FinalizeGoalAsync_MonitoringUsesNoneToken_NotTheCallerToken()
    {
        var monitor = new RecordingCiMonitor();
        var pipeline = CreatePipeline("goal-1", "repo-a");
        var service = CreateLifecycleService(monitor, pipeline);

        using var callerCts = new CancellationTokenSource();
        await service.FinalizeGoalAsync(
            pipeline, GoalStatus.Completed, failureReason: null,
            mergeCommitHash: "sha-a", callerCts.Token);

        await monitor.Invoked.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(CancellationToken.None, monitor.ObservedToken);
        Assert.False(monitor.ObservedToken.CanBeCanceled,
            "Monitoring must not be cancellable by the caller — no outer timeout is applied.");

        // Cancelling the caller after the fact must leave the observed token unaffected.
        await callerCts.CancelAsync();
        Assert.False(monitor.ObservedToken.IsCancellationRequested);
    }

    /// <summary>
    /// Fire-and-forget means the caller must not block on the monitor. The monitor here never
    /// completes; <c>FinalizeGoalAsync</c> must still return promptly.
    /// </summary>
    [Fact]
    public async Task FinalizeGoalAsync_DoesNotAwaitMonitoring()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = new BlockingCiMonitor(release);
        var pipeline = CreatePipeline("goal-1", "repo-a");
        var service = CreateLifecycleService(monitor, pipeline);

        try
        {
            var finalize = service.FinalizeGoalAsync(
                pipeline, GoalStatus.Completed, failureReason: null,
                mergeCommitHash: "sha-a", TestContext.Current.CancellationToken);

            // Completes while the monitor is still blocked → the call is genuinely detached.
            await finalize.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
            Assert.True(finalize.IsCompletedSuccessfully);
            Assert.False(monitor.Finished);
        }
        finally
        {
            release.TrySetResult(true);
        }
    }

    private sealed class BlockingCiMonitor(TaskCompletionSource<bool> release) : CiMonitorService
    {
        public bool Finished { get; private set; }

        public override async Task MonitorGoalAsync(
            string goalId, string commaSeparatedHashes, List<string> repoNames, CancellationToken ct)
        {
            await release.Task;
            Finished = true;
        }
    }

    // ── Negative: excluded states must not launch monitoring ───────────────

    [Theory]
    [InlineData(GoalStatus.Failed)]
    [InlineData(GoalStatus.Cancelled)]
    public async Task FinalizeGoalAsync_NonCompletedStatus_DoesNotLaunchMonitoring(GoalStatus status)
    {
        var monitor = new RecordingCiMonitor();
        var pipeline = CreatePipeline("goal-1", "repo-a");
        var service = CreateLifecycleService(monitor, pipeline);

        await service.FinalizeGoalAsync(
            pipeline, status, failureReason: "boom",
            mergeCommitHash: "sha-a", TestContext.Current.CancellationToken);

        Assert.False(await RanUnexpectedlyAsync(monitor),
            $"Status {status} must not trigger CI monitoring.");
        Assert.Equal(0, monitor.InvocationCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task FinalizeGoalAsync_MissingMergeHash_DoesNotLaunchMonitoring(string? mergeCommitHash)
    {
        var monitor = new RecordingCiMonitor();
        var pipeline = CreatePipeline("goal-1", "repo-a");
        var service = CreateLifecycleService(monitor, pipeline);

        await service.FinalizeGoalAsync(
            pipeline, GoalStatus.Completed, failureReason: null,
            mergeCommitHash, TestContext.Current.CancellationToken);

        Assert.False(await RanUnexpectedlyAsync(monitor),
            "A goal with no merge commit has nothing to monitor.");
        Assert.Equal(0, monitor.InvocationCount);
    }

    [Fact]
    public async Task FinalizeGoalAsync_NoRepositories_DoesNotLaunchMonitoring()
    {
        var monitor = new RecordingCiMonitor();
        var pipeline = CreatePipeline("goal-1"); // no repository names
        var service = CreateLifecycleService(monitor, pipeline);

        await service.FinalizeGoalAsync(
            pipeline, GoalStatus.Completed, failureReason: null,
            mergeCommitHash: "sha-a", TestContext.Current.CancellationToken);

        Assert.False(await RanUnexpectedlyAsync(monitor),
            "With no repositories there is nothing to zip merge hashes against.");
        Assert.Equal(0, monitor.InvocationCount);
    }

    [Fact]
    public async Task FinalizeGoalAsync_NullCiMonitor_CompletesNormally()
    {
        var pipeline = CreatePipeline("goal-1", "repo-a");
        var service = CreateLifecycleService(ciMonitor: null, pipeline);

        // No monitor configured — completion must still succeed (backward compatibility).
        await service.FinalizeGoalAsync(
            pipeline, GoalStatus.Completed, failureReason: null,
            mergeCommitHash: "sha-a", TestContext.Current.CancellationToken);

        Assert.Equal(GoalPhase.Planning, pipeline.Phase); // untouched by finalize itself
    }

    // ── Exception isolation ────────────────────────────────────────────────

    /// <summary>
    /// A throwing monitor must be caught and logged inside the detached task. It must never
    /// surface to the caller, and it must never become an unobserved task exception.
    /// </summary>
    [Fact]
    public async Task FinalizeGoalAsync_MonitorThrows_ExceptionIsCaughtAndLogged()
    {
        var logger = new RecordingLogger<GoalDispatcher>();
        var monitor = new RecordingCiMonitor
        {
            ThrowOnInvoke = new InvalidOperationException("simulated CI monitor failure")
        };
        var pipeline = CreatePipeline("goal-1", "repo-a");
        var service = CreateLifecycleService(monitor, pipeline, logger);

        // Must not throw despite the monitor throwing.
        await service.FinalizeGoalAsync(
            pipeline, GoalStatus.Completed, failureReason: null,
            mergeCommitHash: "sha-a", TestContext.Current.CancellationToken);

        await monitor.Invoked.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

        // Poll briefly: the catch runs on the detached task, just after the throw.
        var logged = false;
        for (var i = 0; i < 50 && !logged; i++)
        {
            logged = logger.Snapshot().Any(e =>
                e.Message.Contains("CI monitoring failed", StringComparison.OrdinalIgnoreCase));
            if (!logged)
                await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.True(logged, "The monitor failure must be logged, not silently dropped.");
        var entry = logger.Snapshot().First(e =>
            e.Message.Contains("CI monitoring failed", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.IsType<InvalidOperationException>(entry.Exception);

        // Force finalizers so an unobserved-exception crash would have surfaced by now.
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    // ── Dispatcher forwarding ──────────────────────────────────────────────

    /// <summary>
    /// The dispatcher must forward its <c>ciMonitor</c> argument into the lifecycle service —
    /// otherwise the DI-registered monitor would silently never be used. Reading the private
    /// field is the only way to observe the internal composition.
    /// </summary>
    [Fact]
    public void GoalDispatcher_ForwardsCiMonitorToLifecycleService()
    {
        var monitor = new RecordingCiMonitor();
        var dispatcher = TestDispatcherFactory.Create(monitor);

        var lifecycle = GetPrivateField<object>(dispatcher, "_lifecycleService");
        Assert.NotNull(lifecycle);

        var forwarded = GetPrivateField<CiMonitorService?>(lifecycle!, "_ciMonitor");
        Assert.Same(monitor, forwarded);
    }

    /// <summary>Without a monitor argument the lifecycle service must hold <c>null</c>.</summary>
    [Fact]
    public void GoalDispatcher_WithoutCiMonitor_LifecycleServiceHoldsNull()
    {
        var dispatcher = TestDispatcherFactory.Create(ciMonitor: null);

        var lifecycle = GetPrivateField<object>(dispatcher, "_lifecycleService");
        Assert.NotNull(lifecycle);

        var forwarded = GetPrivateField<CiMonitorService?>(lifecycle!, "_ciMonitor");
        Assert.Null(forwarded);
    }

    private static T? GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {instance.GetType().Name}.");
        return (T?)field.GetValue(instance);
    }

    private static class TestDispatcherFactory
    {
        public static GoalDispatcher Create(CiMonitorService? ciMonitor)
        {
            var goalManager = new GoalManager();
            goalManager.AddSource(new InMemoryGoalStore());

            return new GoalDispatcher(
                goalManager,
                new GoalPipelineManager(),
                new TaskQueue(),
                new GrpcWorkerGateway(new WorkerPool()),
                new TaskCompletionNotifier(),
                NullLogger<GoalDispatcher>.Instance,
                new CopilotHive.Git.BrainRepoManager(
                    Path.GetTempPath(), NullLogger<CopilotHive.Git.BrainRepoManager>.Instance),
                config: new HiveConfigFile { Orchestrator = new OrchestratorConfig() },
                ciMonitor: ciMonitor);
        }
    }
}
