using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

public class PremiumModelSelectionTests
{
    // ── HiveConfiguration.GetPremiumModelForRole ─────────────────────────

    [Theory]
    [InlineData(WorkerRole.Coder, "gpt-5.4-premium")]
    [InlineData(WorkerRole.Reviewer, "claude-opus-4.6-premium")]
    [InlineData(WorkerRole.Tester, "gpt-5-premium")]
    [InlineData(WorkerRole.Improver, "claude-opus-premium")]
    public void GetPremiumModelForRole_WhenConfigured_ReturnsConfiguredModel(WorkerRole role, string expectedModel)
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
    [InlineData(WorkerRole.Coder)]
    [InlineData(WorkerRole.Reviewer)]
    [InlineData(WorkerRole.Tester)]
    [InlineData(WorkerRole.Improver)]
    [InlineData(WorkerRole.Orchestrator)]
    [InlineData(WorkerRole.DocWriter)]
    public void GetPremiumModelForRole_WhenNotConfigured_ReturnsNull(WorkerRole role)
    {
        var config = new HiveConfiguration
        {
            Goal = "test",
            GitHubToken = "fake",
        };

        Assert.Null(config.GetPremiumModelForRole(role));
    }

    [Fact]
    public void GetPremiumModelForRole_EnumValues_ReturnsCorrectModel()
    {
        var config = new HiveConfiguration
        {
            Goal = "test",
            GitHubToken = "fake",
            PremiumCoderModel = "premium-coder",
        };

        Assert.Equal("premium-coder", config.GetPremiumModelForRole(WorkerRole.Coder));
    }

    // ── Brain model_tier propagation ─────────────────────────────────────

    [Fact]
    public async Task PlanGoalAsync_WhenBrainReturnsPremiumTier_ModelTierIsPremium()
    {
        // Arrange: a brain whose PlanGoalAsync returns model_tier="premium"
        var brain = new PlanGoalPremiumBrain();
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Complex feature" };
        var pipeline = new GoalPipelineManager().CreatePipeline(goal, maxRetries: 3);

        // Act: call PlanGoalAsync and apply the same propagation the dispatcher performs
        var decision = await brain.PlanGoalAsync(pipeline, TestContext.Current.CancellationToken);
        pipeline.LatestModelTier = ModelTierExtensions.ParseModelTier(decision.ModelTier);

        // Assert: both the decision object and the pipeline reflect the premium tier
        Assert.Equal("premium", decision.ModelTier);
        Assert.Equal(ModelTier.Premium, pipeline.LatestModelTier);
    }

    [Fact]
    public async Task DecideNextStepAsync_WhenBrainReturnsPremiumTier_ModelTierIsPremium()
    {
        // Arrange: a brain whose DecideNextStepAsync returns model_tier="premium"
        var brain = new DecideNextStepPremiumBrain();
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Complex next step" };
        var pipeline = new GoalPipelineManager().CreatePipeline(goal, maxRetries: 3);

        // Act
        var decision = await brain.DecideNextStepAsync(pipeline, "What should we do next?", TestContext.Current.CancellationToken);

        // Assert: the returned decision carries the premium tier
        Assert.Equal("premium", decision.ModelTier);
    }

    [Fact]
    public async Task DispatchToRole_WhenPromptIsNull_CraftPromptDoesNotOverwritePremiumTier()
    {
        // Arrange: first decision sets premium tier with a null prompt; CraftPromptAsync returns standard.
        // Without the Bug-2 fix, the premium tier would be silently replaced by "standard".
        var brain = new NullPromptPremiumInterpretBrain();
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

        var (dispatcher, _, taskId, taskQueue) = CreateDispatcher(GoalPhase.Coding, brain, hiveConfigFile);
        taskQueue.OnEnqueue = t => capturedModel = t.Model;

        // Act: simulate the coder completing — InterpretOutputAsync returns premium+null prompt,
        //       then DispatchToRole falls back to CraftPromptAsync which would return standard.
        await dispatcher.HandleTaskCompletionAsync(new Shared.Grpc.TaskComplete
        {
            TaskId = taskId,
            Status = Shared.Grpc.TaskStatus.Completed,
            Output = "Coding complete.",
            GitStatus = new Shared.Grpc.GitStatus { FilesChanged = 3 }, // avoid no-op detection
        }, TestContext.Current.CancellationToken);

        // Assert: the premium tier set by InterpretOutputAsync must survive the CraftPrompt fallback.
        Assert.Equal("premium-coder-model", capturedModel);
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
        }, TestContext.Current.CancellationToken);

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
        }, TestContext.Current.CancellationToken);

        Assert.Equal("standard-coder-model", capturedModel);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static (GoalDispatcher dispatcher, GoalPipeline pipeline, string taskId, TaskQueue taskQueue)
        CreateDispatcher(GoalPhase phase, IDistributedBrain brain, HiveConfigFile? configFile = null, int maxRetries = 3)
    {
        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Test goal",
            RepositoryNames = ["test-repo"],
        };
        var goalSource = new PremiumFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        // Ensure the config has a matching repository entry
        configFile ??= new HiveConfigFile();
        if (!configFile.Repositories.Any(r => r.Name == "test-repo"))
        {
            configFile.Repositories.Add(new RepositoryConfig
            {
                Name = "test-repo",
                Url = "https://example.com/test-repo.git",
                DefaultBranch = "develop",
            });
        }

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
    private readonly ModelTier _modelTierToReturn;

    public CapturingBrain(string modelTierToReturn)
    {
        _modelTierToReturn = ModelTierExtensions.ParseModelTier(modelTierToReturn);
    }

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<OrchestratorDecision> PlanGoalAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult(new OrchestratorDecision { Action = OrchestratorActionType.SpawnCoder });

    public Task<IterationPlan> PlanIterationAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult(IterationPlan.Default());

    public Task<string> CraftPromptAsync(
        GoalPipeline pipeline, WorkerRole role, string? additionalContext = null, CancellationToken ct = default)
    {
        pipeline.LatestModelTier = _modelTierToReturn;
        return Task.FromResult($"Work on {pipeline.Description} as {role.ToRoleName()}");
    }

    public Task<OrchestratorDecision> InterpretOutputAsync(GoalPipeline pipeline, GoalPhase phase, string workerOutput, CancellationToken ct = default) =>
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

file sealed class PremiumFakeGoalSource: IGoalSource
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

/// <summary>
/// Brain stub whose <see cref="PlanGoalAsync"/> returns <c>model_tier = "premium"</c>.
/// </summary>
file sealed class PlanGoalPremiumBrain : IDistributedBrain
{
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<OrchestratorDecision> PlanGoalAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult(new OrchestratorDecision
        {
            Action = OrchestratorActionType.SpawnCoder,
            Prompt = "Implement the complex feature",
            ModelTier = "premium",
        });

    public Task<IterationPlan> PlanIterationAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult(IterationPlan.Default());

    public Task<string> CraftPromptAsync(
        GoalPipeline pipeline, WorkerRole role, string? additionalContext = null, CancellationToken ct = default)
    {
        pipeline.LatestModelTier = ModelTier.Standard;
        return Task.FromResult($"Work on {pipeline.Description} as {role.ToRoleName()}");
    }

    public Task<OrchestratorDecision> InterpretOutputAsync(GoalPipeline pipeline, GoalPhase phase, string workerOutput, CancellationToken ct = default) =>
        Task.FromResult(new OrchestratorDecision { Action = OrchestratorActionType.Done, Verdict = "PASS" });

    public Task<OrchestratorDecision> DecideNextStepAsync(
        GoalPipeline pipeline, string context, CancellationToken ct = default) =>
        Task.FromResult(new OrchestratorDecision { Action = OrchestratorActionType.Done });

    public Task InformAsync(GoalPipeline pipeline, string information, CancellationToken ct = default) =>
        Task.CompletedTask;
}

/// <summary>
/// Brain stub whose <see cref="DecideNextStepAsync"/> returns <c>model_tier = "premium"</c>.
/// </summary>
file sealed class DecideNextStepPremiumBrain : IDistributedBrain
{
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<OrchestratorDecision> PlanGoalAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult(new OrchestratorDecision { Action = OrchestratorActionType.SpawnCoder });

    public Task<IterationPlan> PlanIterationAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult(IterationPlan.Default());

    public Task<string> CraftPromptAsync(
        GoalPipeline pipeline, WorkerRole role, string? additionalContext = null, CancellationToken ct = default)
    {
        pipeline.LatestModelTier = ModelTier.Standard;
        return Task.FromResult($"Work on {pipeline.Description} as {role.ToRoleName()}");
    }

    public Task<OrchestratorDecision> InterpretOutputAsync(GoalPipeline pipeline, GoalPhase phase, string workerOutput, CancellationToken ct = default) =>
        Task.FromResult(new OrchestratorDecision { Action = OrchestratorActionType.Done, Verdict = "PASS" });

    public Task<OrchestratorDecision> DecideNextStepAsync(
        GoalPipeline pipeline, string context, CancellationToken ct = default) =>
        Task.FromResult(new OrchestratorDecision
        {
            Action = OrchestratorActionType.SpawnCoder,
            Prompt = "Implement the next step",
            ModelTier = "premium",
        });

    public Task InformAsync(GoalPipeline pipeline, string information, CancellationToken ct = default) =>
        Task.CompletedTask;
}

/// <summary>
/// Brain stub that returns <c>model_tier = "premium"</c> with a <c>null</c> prompt from
/// <see cref="InterpretOutputAsync"/>, then returns <c>model_tier = "standard"</c> from
/// <see cref="CraftPromptAsync"/>. Used to verify Bug 2: the premium tier must survive
/// the CraftPromptAsync fallback inside <c>DispatchToRole</c>.
/// </summary>
file sealed class NullPromptPremiumInterpretBrain : IDistributedBrain
{
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<OrchestratorDecision> PlanGoalAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult(new OrchestratorDecision { Action = OrchestratorActionType.SpawnCoder });

    public Task<IterationPlan> PlanIterationAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult(IterationPlan.Default());

    /// <summary>
    /// Mimics a Brain that decides to spawn another coder with premium tier but leaves the prompt
    /// to be crafted by the fallback path — this is the scenario that triggers Bug 2.
    /// </summary>
    public Task<OrchestratorDecision> InterpretOutputAsync(GoalPipeline pipeline, GoalPhase phase, string workerOutput, CancellationToken ct = default) =>
        Task.FromResult(new OrchestratorDecision
        {
            Action = OrchestratorActionType.SpawnCoder,
            Prompt = null,   // null prompt → DispatchToRole will call CraftPromptAsync
            ModelTier = "premium",
        });

    /// <summary>
    /// Returns a valid prompt but sets model tier to "standard" — simulating a Brain that
    /// does not escalate on the craft-prompt call. Without the Bug-2 fix this would silently
    /// overwrite the "premium" tier from the earlier InterpretOutputAsync decision.
    /// </summary>
    public Task<string> CraftPromptAsync(
        GoalPipeline pipeline, WorkerRole role, string? additionalContext = null, CancellationToken ct = default)
    {
        pipeline.LatestModelTier = ModelTier.Standard;
        return Task.FromResult($"Retry: work on {pipeline.Description} as {role.ToRoleName()}");
    }

    public Task<OrchestratorDecision> DecideNextStepAsync(
        GoalPipeline pipeline, string context, CancellationToken ct = default) =>
        Task.FromResult(new OrchestratorDecision { Action = OrchestratorActionType.Done });

    public Task InformAsync(GoalPipeline pipeline, string information, CancellationToken ct = default) =>
        Task.CompletedTask;
}
