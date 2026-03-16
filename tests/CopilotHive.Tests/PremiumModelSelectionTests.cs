using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

public class PremiumModelSelectionTests
{
    // ── HiveConfiguration.GetPremiumModelForRole ─────────────────────────

    [Theory]
    [InlineData("coder", "gpt-5.4-premium")]
    [InlineData("reviewer", "claude-opus-4.6-premium")]
    [InlineData("tester", "gpt-5-premium")]
    [InlineData("improver", "claude-opus-premium")]
    public void GetPremiumModelForRole_WhenConfigured_ReturnsConfiguredModel(string role, string expectedModel)
    {
        var config = new HiveConfiguration
        {
            Goal = "test",
            GitHubToken = "fake",
            PremiumCoderModel = "gpt-5.4-premium",
            PremiumReviewerModel = "claude-opus-4.6-premium",
            PremiumTesterModel = "gpt-5-premium",
            PremiumImproverModel = "claude-opus-premium",
        };

        Assert.Equal(expectedModel, config.GetPremiumModelForRole(role));
    }

    [Theory]
    [InlineData("coder")]
    [InlineData("reviewer")]
    [InlineData("tester")]
    [InlineData("improver")]
    [InlineData("orchestrator")]
    [InlineData("unknown")]
    public void GetPremiumModelForRole_WhenNotConfigured_ReturnsNull(string role)
    {
        var config = new HiveConfiguration
        {
            Goal = "test",
            GitHubToken = "fake",
        };

        Assert.Null(config.GetPremiumModelForRole(role));
    }

    [Fact]
    public void GetPremiumModelForRole_CaseInsensitive()
    {
        var config = new HiveConfiguration
        {
            Goal = "test",
            GitHubToken = "fake",
            PremiumCoderModel = "premium-coder",
        };

        Assert.Equal("premium-coder", config.GetPremiumModelForRole("CODER"));
        Assert.Equal("premium-coder", config.GetPremiumModelForRole("Coder"));
    }

    // ── GoalDispatcher premium model selection ───────────────────────────

    [Fact]
    public async Task DispatchToRole_WhenPremiumTierAndPremiumModelConfigured_UsesPremiumModel()
    {
        var brain = new CapturingBrain(modelTierToReturn: "premium");
        var capturedModel = (string?)null;

        var hiveConfigFile = new HiveConfigFile
        {
            Workers =
            {
                ["coder"] = new WorkerConfig
                {
                    Model = "standard-coder-model",
                    PremiumModel = "premium-coder-model",
                },
            },
        };

        var (dispatcher, pipeline, taskId, taskQueue) = CreateDispatcher(GoalPhase.Coding, brain, hiveConfigFile);
        taskQueue.OnEnqueue = t => capturedModel = t.Model;

        await dispatcher.HandleTaskCompletionAsync(new Shared.Grpc.TaskComplete
        {
            TaskId = taskId,
            Status = Shared.Grpc.TaskStatus.Completed,
            Output = "Done.",
        });

        Assert.Equal("premium-coder-model", capturedModel);
    }

    [Fact]
    public async Task DispatchToRole_WhenPremiumTierButNoPremiumModelConfigured_FallsBackToStandardModel()
    {
        var brain = new CapturingBrain(modelTierToReturn: "premium");
        var capturedModel = (string?)null;

        var hiveConfigFile = new HiveConfigFile
        {
            Workers =
            {
                ["coder"] = new WorkerConfig
                {
                    Model = "standard-coder-model",
                },
            },
        };

        var (dispatcher, pipeline, taskId, taskQueue) = CreateDispatcher(GoalPhase.Coding, brain, hiveConfigFile);
        taskQueue.OnEnqueue = t => capturedModel = t.Model;

        await dispatcher.HandleTaskCompletionAsync(new Shared.Grpc.TaskComplete
        {
            TaskId = taskId,
            Status = Shared.Grpc.TaskStatus.Completed,
            Output = "Done.",
        });

        Assert.Equal("standard-coder-model", capturedModel);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static (GoalDispatcher dispatcher, GoalPipeline pipeline, string taskId, TaskQueue taskQueue)
        CreateDispatcher(GoalPhase phase, IDistributedBrain brain, HiveConfigFile? configFile = null, int maxRetries = 3)
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
        var goalSource = new PremiumFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries);
        pipeline.AdvanceTo(phase);

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
            NullLogger<GoalDispatcher>.Instance,
            brain,
            configFile);

        return (dispatcher, pipeline, taskId, taskQueue);
    }
}

/// <summary>
/// A brain stub that returns a configurable <c>model_tier</c> via <see cref="CraftPromptAsync"/>
/// by setting <see cref="GoalPipeline.LatestModelTier"/> directly, mirroring what the real brain does.
/// </summary>
file sealed class CapturingBrain : IDistributedBrain
{
    private readonly string _modelTierToReturn;

    public CapturingBrain(string modelTierToReturn)
    {
        _modelTierToReturn = modelTierToReturn;
    }

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<OrchestratorDecision> PlanGoalAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult(new OrchestratorDecision { Action = OrchestratorActionType.SpawnCoder });

    public Task<IterationPlan> PlanIterationAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult(IterationPlan.Default());

    public Task<string> CraftPromptAsync(
        GoalPipeline pipeline, string workerRole, string? additionalContext = null, CancellationToken ct = default)
    {
        pipeline.LatestModelTier = _modelTierToReturn;
        return Task.FromResult($"Work on {pipeline.Description} as {workerRole}");
    }

    public Task<OrchestratorDecision> InterpretOutputAsync(
        GoalPipeline pipeline, string workerRole, string workerOutput, CancellationToken ct = default) =>
        Task.FromResult(new OrchestratorDecision
        {
            Action = OrchestratorActionType.Done,
            Verdict = "PASS",
        });

    public Task<OrchestratorDecision> DecideNextStepAsync(
        GoalPipeline pipeline, string context, CancellationToken ct = default) =>
        Task.FromResult(new OrchestratorDecision { Action = OrchestratorActionType.Done });

    public Task InformAsync(GoalPipeline pipeline, string information, CancellationToken ct = default) =>
        Task.CompletedTask;
}

file sealed class PremiumFakeGoalSource : IGoalSource
{
    private readonly Goal _goal;

    public PremiumFakeGoalSource(Goal goal) => _goal = goal;

    public string Name => "premium-fake";

    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([_goal]);

    public Task UpdateGoalStatusAsync(
        string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
        Task.CompletedTask;
}
