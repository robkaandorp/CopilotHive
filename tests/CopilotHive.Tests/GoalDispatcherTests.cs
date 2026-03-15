using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

public sealed class GoalDispatcherReviewVerdictTests
{
    // ── ReviewVerdict mapping ────────────────────────────────────────────

    [Fact]
    public async Task ReviewPhase_VerdictFail_SetsReviewVerdictRequestChanges()
    {
        var brain = new FakeDispatcherBrain
        {
            InterpretOutputOverride = (_, _, _) => new OrchestratorDecision
            {
                Action = OrchestratorActionType.Done,
                Verdict = "FAIL",
            },
        };

        // maxRetries=0 so the FAIL-verdict retry path calls MarkGoalFailed instead of
        // re-dispatching to Coder, keeping the test self-contained.
        var (dispatcher, pipeline, taskId) = CreateDispatcher(GoalPhase.Review, brain, maxRetries: 0);

        await dispatcher.HandleTaskCompletionAsync(new TaskComplete
        {
            TaskId = taskId,
            Status = Shared.Grpc.TaskStatus.Completed,
            Output = "Several critical issues found.",
        });

        Assert.Equal("REQUEST_CHANGES", pipeline.Metrics.ReviewVerdict);
    }

    [Fact]
    public async Task ReviewPhase_VerdictPass_SetsReviewVerdictApprove()
    {
        var brain = new FakeDispatcherBrain
        {
            InterpretOutputOverride = (_, _, _) => new OrchestratorDecision
            {
                Action = OrchestratorActionType.Done,
                Verdict = "PASS",
            },
        };

        var (dispatcher, pipeline, taskId) = CreateDispatcher(GoalPhase.Review, brain);

        await dispatcher.HandleTaskCompletionAsync(new TaskComplete
        {
            TaskId = taskId,
            Status = Shared.Grpc.TaskStatus.Completed,
            Output = "LGTM, no issues found.",
        });

        Assert.Equal("APPROVE", pipeline.Metrics.ReviewVerdict);
    }

    [Theory]
    [InlineData(GoalPhase.Coding, "PASS")]
    [InlineData(GoalPhase.Coding, "FAIL")]
    [InlineData(GoalPhase.Testing, "FAIL")]
    public async Task NonReviewPhase_AnyVerdict_ReviewVerdictRemainsEmpty(GoalPhase phase, string verdict)
    {
        var brain = new FakeDispatcherBrain
        {
            InterpretOutputOverride = (_, _, _) => new OrchestratorDecision
            {
                Action = OrchestratorActionType.Done,
                Verdict = verdict,
            },
        };

        // maxRetries=0 prevents retry dispatching so the test stays self-contained.
        var (dispatcher, pipeline, taskId) = CreateDispatcher(phase, brain, maxRetries: 0);

        await dispatcher.HandleTaskCompletionAsync(new TaskComplete
        {
            TaskId = taskId,
            Status = Shared.Grpc.TaskStatus.Completed,
            Output = "Worker output.",
        });

        Assert.True(
            pipeline.Metrics.ReviewVerdict is null or "",
            $"Expected ReviewVerdict to be null or empty for phase {phase} with verdict {verdict}, " +
            $"but was: '{pipeline.Metrics.ReviewVerdict}'");
    }

    // ── ReviewVerdict fallback: unknown Verdict uses ReviewVerdict field ─

    [Fact]
    public async Task ReviewPhase_UnknownVerdict_FallsBackToReviewVerdictField()
    {
        var brain = new FakeDispatcherBrain
        {
            InterpretOutputOverride = (_, _, _) => new OrchestratorDecision
            {
                Action = OrchestratorActionType.Done,
                Verdict = null,
                ReviewVerdict = "APPROVED",
            },
        };

        var (dispatcher, pipeline, taskId) = CreateDispatcher(GoalPhase.Review, brain);

        await dispatcher.HandleTaskCompletionAsync(new TaskComplete
        {
            TaskId = taskId,
            Status = Shared.Grpc.TaskStatus.Completed,
            Output = "Looks good overall.",
        });

        Assert.Equal("APPROVED", pipeline.Metrics.ReviewVerdict);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal self-contained <see cref="GoalDispatcher"/> for testing the
    /// ReviewVerdict population logic in <c>DriveNextPhaseAsync</c>.
    /// </summary>
    private static (GoalDispatcher dispatcher, GoalPipeline pipeline, string taskId)
        CreateDispatcher(GoalPhase phase, IDistributedBrain brain, int maxRetries = 3)
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
        var goalSource = new FakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);

        // Populate the internal goal→source map so UpdateGoalStatusAsync doesn't throw.
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries);
        pipeline.AdvanceTo(phase);

        var taskId = $"task-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);

        var notifier = new TaskCompletionNotifier();
        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new WorkerPool(),
            notifier,
            NullLogger<GoalDispatcher>.Instance,
            brain);

        return (dispatcher, pipeline, taskId);
    }
}

/// <summary>
/// Minimal <see cref="IDistributedBrain"/> stub for GoalDispatcher tests.
/// </summary>
file sealed class FakeDispatcherBrain : IDistributedBrain
{
    public Func<GoalPipeline, string, string, OrchestratorDecision>? InterpretOutputOverride { get; set; }

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<OrchestratorDecision> PlanGoalAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult(new OrchestratorDecision { Action = OrchestratorActionType.SpawnCoder });

    public Task<IterationPlan> PlanIterationAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult(IterationPlan.Default());

    public Task<string> CraftPromptAsync(
        GoalPipeline pipeline, string workerRole, string? additionalContext = null, CancellationToken ct = default) =>
        Task.FromResult($"Work on {pipeline.Description} as {workerRole}");

    public Task<OrchestratorDecision> InterpretOutputAsync(
        GoalPipeline pipeline, string workerRole, string workerOutput, CancellationToken ct = default)
    {
        var decision = InterpretOutputOverride?.Invoke(pipeline, workerRole, workerOutput)
            ?? new OrchestratorDecision { Action = OrchestratorActionType.Done, Verdict = "PASS" };
        return Task.FromResult(decision);
    }

    public Task<OrchestratorDecision> DecideNextStepAsync(
        GoalPipeline pipeline, string context, CancellationToken ct = default) =>
        Task.FromResult(new OrchestratorDecision { Action = OrchestratorActionType.Done });

    public Task InformAsync(GoalPipeline pipeline, string information, CancellationToken ct = default) =>
        Task.CompletedTask;
}

/// <summary>
/// Minimal <see cref="IGoalSource"/> that returns a single pre-configured goal.
/// </summary>
file sealed class FakeGoalSource : IGoalSource
{
    private readonly Goal _goal;

    public FakeGoalSource(Goal goal) => _goal = goal;

    public string Name => "fake";

    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([_goal]);

    public Task UpdateGoalStatusAsync(
        string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
        Task.CompletedTask;
}
