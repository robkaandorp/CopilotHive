using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

public class PremiumModelSelectionTests
{
    // -- HiveConfiguration.GetPremiumModelForRole --

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

    // -- GoalDispatcher premium model selection --

    [Fact]
    public async Task DispatchToRole_WhenPremiumTierAndPremiumModelConfigured_UsesPremiumModel()
    {
        var brain = new CapturingBrain(modelTierToReturn: "premium");
        var capturedModel = (string?)null;

        // When Coding completes PASS, state machine advances to Testing.
        // Configure the tester model since that's the next worker dispatched.
        var hiveConfigFile = new HiveConfigFile
        {
            Workers =
            {
                ["tester"] = new WorkerConfig
                {
                    Model = "standard-tester-model",
                    PremiumModel = "premium-tester-model",
                },
            },
        };

        var (dispatcher, pipeline, taskId, taskQueue) = CreateDispatcher(GoalPhase.Coding, brain, hiveConfigFile);
        taskQueue.OnEnqueue = t => capturedModel = t.Model;

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "Done.",
            Metrics = new TaskMetrics { Verdict = "PASS" },
            GitStatus = new GitChangeSummary { FilesChanged = 3 },
        }, TestContext.Current.CancellationToken);

        Assert.Equal("premium-tester-model", capturedModel);
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
                ["tester"] = new WorkerConfig
                {
                    Model = "standard-tester-model",
                },
            },
        };

        var (dispatcher, pipeline, taskId, taskQueue) = CreateDispatcher(GoalPhase.Coding, brain, hiveConfigFile);
        taskQueue.OnEnqueue = t => capturedModel = t.Model;

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "Done.",
            Metrics = new TaskMetrics { Verdict = "PASS" },
            GitStatus = new GitChangeSummary { FilesChanged = 3 },
        }, TestContext.Current.CancellationToken);

        Assert.Equal("standard-tester-model", capturedModel);
    }

    // -- Helpers --

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

        // Use the brain's plan so per-phase model tiers are applied
        var plan = brain.PlanIterationAsync(pipeline).GetAwaiter().GetResult();
        pipeline.SetPlan(plan);
        pipeline.StateMachine.StartIteration(plan.Phases);

        var taskId = $"task-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);

        var notifier = new TaskCompletionNotifier();
        var taskQueue = new TaskQueue();
        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            taskQueue,
            new GrpcWorkerGateway(new WorkerPool()),
            notifier,
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain,
            configFile);

        return (dispatcher, pipeline, taskId, taskQueue);
    }
}

/// <summary>
/// A brain stub that returns an iteration plan with configurable per-phase model tiers.
/// Sets the requested tier on ALL phases so the dispatcher picks it up regardless of which phase runs next.
/// </summary>
file sealed class CapturingBrain : IDistributedBrain
{
    private readonly ModelTier _modelTierToReturn;

    public CapturingBrain(string modelTierToReturn)
    {
        _modelTierToReturn = ModelTierExtensions.ParseModelTier(modelTierToReturn);
    }

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IterationPlan> PlanIterationAsync(GoalPipeline pipeline, CancellationToken ct = default)
    {
        var plan = IterationPlan.Default();

        // Set the requested tier on phases that have workers
        foreach (var phase in plan.Phases)
        {
            if (phase is GoalPhase.Planning or GoalPhase.Merging or GoalPhase.Done or GoalPhase.Failed)
                continue;
            plan.PhaseTiers[phase] = _modelTierToReturn;
        }

        return Task.FromResult(plan);
    }

    public Task<string> CraftPromptAsync(
        GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default)
    {
        return Task.FromResult($"Work on {pipeline.Description} as {phase}");
    }

    public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) => Task.CompletedTask;

    public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) => Task.CompletedTask;

    public BrainStats? GetStats() => null;
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
