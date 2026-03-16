using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

public sealed class GoalDispatcherAgentsMdSizeLimitTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AgentsMdUnderLimit_NoRetry()
    {
        var shortContent = new string('x', 3999);
        var (configRepo, _) = CreateTempAgentsRepo("improver", shortContent);

        var brain = new FakeImproverBrain();
        var (dispatcher, pipeline, taskId, _) = CreateImproverDispatcher(brain, configRepo);

        await dispatcher.HandleTaskCompletionAsync(new TaskComplete
        {
            TaskId = taskId,
            Status = Shared.Grpc.TaskStatus.Completed,
            Output = "Improved agents.md files.",
        });

        Assert.Equal(0, pipeline.ImproverRetries);
    }

    [Fact]
    public async Task AgentsMdOverLimit_TriggersRetryPrompt()
    {
        var longContent = new string('x', 4001);
        var (configRepo, _) = CreateTempAgentsRepo("improver", longContent);

        var brain = new FakeImproverBrain();
        var (dispatcher, pipeline, taskId, taskQueue) = CreateImproverDispatcher(brain, configRepo);

        await dispatcher.HandleTaskCompletionAsync(new TaskComplete
        {
            TaskId = taskId,
            Status = Shared.Grpc.TaskStatus.Completed,
            Output = "Improved agents.md files.",
        });

        Assert.Equal(1, pipeline.ImproverRetries);

        // Verify the improver was re-dispatched with a prompt that names the role and char count
        var retryTask = taskQueue.TryDequeue(WorkerRole.Improver);
        Assert.NotNull(retryTask);
        Assert.Contains("4001", retryTask.Prompt);
        Assert.Contains("improver", retryTask.Prompt);
    }

    [Fact]
    public async Task AgentsMdOverLimitAfter3Retries_DiscardsChanges()
    {
        var longContent = new string('x', 4001);
        var (configRepo, _) = CreateTempAgentsRepo("improver", longContent);

        var logger = new CapturingLogger<GoalDispatcher>();
        var brain = new FakeImproverBrain();
        var (dispatcher, pipeline, taskId, taskQueue) = CreateImproverDispatcher(brain, configRepo, logger);

        // Simulate 3 prior improver retries already consumed
        pipeline.IncrementImproverRetry();
        pipeline.IncrementImproverRetry();
        pipeline.IncrementImproverRetry();

        await dispatcher.HandleTaskCompletionAsync(new TaskComplete
        {
            TaskId = taskId,
            Status = Shared.Grpc.TaskStatus.Completed,
            Output = "Improved agents.md files.",
        });

        // Retries should not have increased beyond 3
        Assert.Equal(3, pipeline.ImproverRetries);

        // Improver should NOT have been re-dispatched
        Assert.Null(taskQueue.TryDequeue(WorkerRole.Improver));

        // A warning about discarding changes should have been logged
        Assert.Contains(logger.Warnings, w => w.Contains("still exceeds") && w.Contains("improver"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private (ConfigRepoManager configRepo, string tempDir) CreateTempAgentsRepo(string role, string content)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-agents-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "agents"));
        File.WriteAllText(Path.Combine(tempDir, "agents", $"{role}.agents.md"), content);
        _tempDirs.Add(tempDir);
        var configRepo = new ConfigRepoManager("https://example.com/config.git", tempDir);
        return (configRepo, tempDir);
    }

    private static (GoalDispatcher dispatcher, GoalPipeline pipeline, string taskId, TaskQueue taskQueue)
        CreateImproverDispatcher(
            IDistributedBrain brain,
            ConfigRepoManager configRepo,
            ILogger<GoalDispatcher>? logger = null)
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
        var goalSource = new FakeImproverGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        pipeline.AdvanceTo(GoalPhase.Improve);

        var taskId = $"task-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);

        var notifier = new TaskCompletionNotifier();
        var taskQueue = new TaskQueue();

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            taskQueue,
            new WorkerPool(),
            notifier,
            logger ?? NullLogger<GoalDispatcher>.Instance,
            brain,
            configRepo: configRepo);

        return (dispatcher, pipeline, taskId, taskQueue);
    }
}

/// <summary>
/// Minimal <see cref="IGoalSource"/> that returns a single pre-configured goal.
/// </summary>
file sealed class FakeImproverGoalSource : IGoalSource
{
    private readonly Goal _goal;

    public FakeImproverGoalSource(Goal goal) => _goal = goal;

    public string Name => "fake-improver";

    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([_goal]);

    public Task UpdateGoalStatusAsync(
        string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
        Task.CompletedTask;
}
file sealed class FakeImproverBrain : IDistributedBrain
{
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<OrchestratorDecision> PlanGoalAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult(new OrchestratorDecision { Action = OrchestratorActionType.SpawnCoder });

    public Task<IterationPlan> PlanIterationAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult(IterationPlan.Default());

    public Task<string> CraftPromptAsync(
        GoalPipeline pipeline, string workerRole, string? additionalContext = null, CancellationToken ct = default) =>
        Task.FromResult($"Work on {pipeline.Description} as {workerRole}");

    public Task<OrchestratorDecision> InterpretOutputAsync(
        GoalPipeline pipeline, string workerRole, string workerOutput, CancellationToken ct = default) =>
        Task.FromResult(new OrchestratorDecision { Action = OrchestratorActionType.Done });

    public Task<OrchestratorDecision> DecideNextStepAsync(
        GoalPipeline pipeline, string context, CancellationToken ct = default) =>
        Task.FromResult(new OrchestratorDecision { Action = OrchestratorActionType.Done });

    public Task InformAsync(GoalPipeline pipeline, string information, CancellationToken ct = default) =>
        Task.CompletedTask;
}

/// <summary>
/// Simple logger that captures warning messages for assertion in tests.
/// </summary>
file sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Warnings { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Warning)
            Warnings.Add(formatter(state, exception));
    }
}
