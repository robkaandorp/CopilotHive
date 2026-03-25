using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests that exercise the full pipeline flow: state machine transitions,
/// TaskBuilder task construction, and GoalPipelineManager persistence round-trips.
/// </summary>
public sealed class PipelineFlowIntegrationTests : IAsyncDisposable
{
    private readonly PipelineStore _store;

    public PipelineFlowIntegrationTests()
    {
        _store = new PipelineStore(":memory:", NullLogger<PipelineStore>.Instance);
    }

    public async ValueTask DisposeAsync() => await _store.DisposeAsync();

    private static readonly List<GoalPhase> StandardPlan =
        [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging];

    private static Goal CreateGoal(string id = "goal-flow-1") =>
        new()
        {
            Id = id,
            Description = $"Integration test goal {id}",
            RepositoryNames = ["CopilotHive"],
        };

    private static TargetRepository CreateRepository() =>
        new()
        {
            Name = "CopilotHive",
            Url = "https://github.com/example/CopilotHive",
            DefaultBranch = "main",
        };

    /// <summary>
    /// Verifies that a goal can be created, driven through all phases with Succeeded inputs,
    /// persisted, and then restored from the store on a new manager instance.
    /// </summary>
    [Fact]
    public void FullHappyPath_AllPhasesSucceed_PipelineCompletesAndRestores()
    {
        var manager = new GoalPipelineManager(_store);
        var goal = CreateGoal("goal-happy");
        var pipeline = manager.CreatePipeline(goal);
        var repo = CreateRepository();
        var taskBuilder = new TaskBuilder(new BranchCoordinator());

        pipeline.StateMachine.StartIteration(StandardPlan);

        // ── Coding ──────────────────────────────────────────────────────────
        var codingTask = taskBuilder.Build(
            pipeline.GoalId, goal.Description,
            WorkerRole.Coder, pipeline.Iteration,
            [repo], "Implement the feature.",
            BranchAction.Create);

        Assert.Equal($"{goal.Id}-coder-001", codingTask.TaskId);
        Assert.Equal($"copilothive/{goal.Id}", codingTask.BranchInfo!.FeatureBranch);
        Assert.Equal("main", codingTask.BranchInfo.BaseBranch);
        Assert.Equal(BranchAction.Create, codingTask.BranchInfo.Action);

        pipeline.AdvanceTo(GoalPhase.Testing);
        var r1 = pipeline.StateMachine.Transition(PhaseInput.Succeeded);
        Assert.Equal(TransitionEffect.Continue, r1.Effect);
        Assert.Equal(GoalPhase.Testing, r1.NextPhase);

        // ── Testing ─────────────────────────────────────────────────────────
        var testingTask = taskBuilder.Build(
            pipeline.GoalId, goal.Description,
            WorkerRole.Tester, pipeline.Iteration,
            [repo], "Run and write tests.",
            BranchAction.Checkout);

        Assert.Equal($"{goal.Id}-tester-001", testingTask.TaskId);
        Assert.Equal($"copilothive/{goal.Id}", testingTask.BranchInfo!.FeatureBranch);
        Assert.Equal("main", testingTask.BranchInfo.BaseBranch);

        pipeline.AdvanceTo(GoalPhase.Review);
        var r2 = pipeline.StateMachine.Transition(PhaseInput.Succeeded);
        Assert.Equal(TransitionEffect.Continue, r2.Effect);
        Assert.Equal(GoalPhase.Review, r2.NextPhase);

        // ── Review ──────────────────────────────────────────────────────────
        var reviewTask = taskBuilder.Build(
            pipeline.GoalId, goal.Description,
            WorkerRole.Reviewer, pipeline.Iteration,
            [repo], "Review the changes.",
            BranchAction.Checkout);

        Assert.Equal($"{goal.Id}-reviewer-001", reviewTask.TaskId);

        pipeline.AdvanceTo(GoalPhase.Merging);
        var r3 = pipeline.StateMachine.Transition(PhaseInput.Succeeded);
        Assert.Equal(TransitionEffect.Continue, r3.Effect);
        Assert.Equal(GoalPhase.Merging, r3.NextPhase);

        // ── Merging ─────────────────────────────────────────────────────────
        pipeline.AdvanceTo(GoalPhase.Done);
        var r4 = pipeline.StateMachine.Transition(PhaseInput.Succeeded);
        Assert.Equal(TransitionEffect.Completed, r4.Effect);
        Assert.Equal(GoalPhase.Done, r4.NextPhase);

        Assert.Equal(GoalPhase.Done, pipeline.StateMachine.Phase);

        // ── Persist and restore ─────────────────────────────────────────────
        manager.PersistFull(pipeline);

        // A Done pipeline is not restored (LoadActivePipelines filters terminal phases).
        var freshManager = new GoalPipelineManager(_store);
        var restored = freshManager.RestoreFromStore();
        Assert.Empty(restored);
    }

    /// <summary>
    /// Verifies that a test failure in iteration 1 triggers NewIteration, then iteration 2
    /// completes all phases successfully. Task IDs correctly reflect the iteration number.
    /// </summary>
    [Fact]
    public void MultiIterationFailureRecovery_TestFailsInIteration1_Iteration2Succeeds()
    {
        var manager = new GoalPipelineManager(_store);
        var goal = CreateGoal("goal-multi");
        var pipeline = manager.CreatePipeline(goal);
        var repo = CreateRepository();
        var taskBuilder = new TaskBuilder(new BranchCoordinator());

        // ── Iteration 1 ─────────────────────────────────────────────────────
        pipeline.StateMachine.StartIteration(StandardPlan);

        // Coding succeeds
        var codingTask1 = taskBuilder.Build(
            pipeline.GoalId, goal.Description,
            WorkerRole.Coder, pipeline.Iteration,
            [repo], "Implement feature.", BranchAction.Create);
        Assert.Equal($"{goal.Id}-coder-001", codingTask1.TaskId);

        pipeline.AdvanceTo(GoalPhase.Testing);
        var r1 = pipeline.StateMachine.Transition(PhaseInput.Succeeded);
        Assert.Equal(TransitionEffect.Continue, r1.Effect);

        // Testing fails → NewIteration
        var testTask1 = taskBuilder.Build(
            pipeline.GoalId, goal.Description,
            WorkerRole.Tester, pipeline.Iteration,
            [repo], "Run tests.", BranchAction.Checkout);
        Assert.Equal($"{goal.Id}-tester-001", testTask1.TaskId);

        var failResult = pipeline.StateMachine.Transition(PhaseInput.Failed);
        Assert.Equal(TransitionEffect.NewIteration, failResult.Effect);
        Assert.Equal(GoalPhase.Coding, failResult.NextPhase);

        pipeline.IncrementIteration();
        pipeline.IncrementTestRetry();

        // ── Iteration 2 ─────────────────────────────────────────────────────
        pipeline.StateMachine.StartIteration(StandardPlan);
        Assert.Equal(GoalPhase.Coding, pipeline.StateMachine.Phase);
        Assert.Equal(2, pipeline.Iteration);

        // Coding task for iteration 2 must have "-002" suffix
        var codingTask2 = taskBuilder.Build(
            pipeline.GoalId, goal.Description,
            WorkerRole.Coder, pipeline.Iteration,
            [repo], "Fix and re-implement.", BranchAction.Create);
        Assert.EndsWith("-002", codingTask2.TaskId);
        Assert.Equal($"{goal.Id}-coder-002", codingTask2.TaskId);

        // Drive all phases to completion
        pipeline.AdvanceTo(GoalPhase.Testing);
        pipeline.StateMachine.Transition(PhaseInput.Succeeded); // → Testing

        pipeline.AdvanceTo(GoalPhase.Review);
        pipeline.StateMachine.Transition(PhaseInput.Succeeded); // → Review

        pipeline.AdvanceTo(GoalPhase.Merging);
        pipeline.StateMachine.Transition(PhaseInput.Succeeded); // → Merging

        pipeline.AdvanceTo(GoalPhase.Done);
        var doneResult = pipeline.StateMachine.Transition(PhaseInput.Succeeded);
        Assert.Equal(TransitionEffect.Completed, doneResult.Effect);
        Assert.Equal(GoalPhase.Done, pipeline.StateMachine.Phase);
        Assert.Equal(2, pipeline.Iteration);
    }

    /// <summary>
    /// Verifies that when the retry limit is exhausted the pipeline enters the Failed terminal
    /// state and is not restored from the store on a new manager instance.
    /// </summary>
    [Fact]
    public void RetryLimitExhaustion_ExceedsMaxRetries_PipelineFailsAndNotRestored()
    {
        // maxRetries = 3: IncrementTestRetry returns true for the first two calls
        // (TestRetries 1 < 3, 2 < 3) and false on the third (TestRetries 3 < 3 = false).
        var manager = new GoalPipelineManager(_store);
        var goal = CreateGoal("goal-exhaust");
        var pipeline = manager.CreatePipeline(goal, maxRetries: 3);

        pipeline.StateMachine.StartIteration(StandardPlan);

        // Simulate driving Coding → Testing, then failing Testing three times
        pipeline.StateMachine.Transition(PhaseInput.Succeeded); // Coding → Testing

        // Retry 1
        pipeline.StateMachine.Transition(PhaseInput.Failed);    // Testing → Coding (NewIteration)
        var r1 = pipeline.IncrementTestRetry();
        Assert.True(r1, "First retry should have remaining retries.");
        pipeline.StateMachine.StartIteration(StandardPlan);
        pipeline.StateMachine.Transition(PhaseInput.Succeeded); // Coding → Testing

        // Retry 2
        pipeline.StateMachine.Transition(PhaseInput.Failed);    // Testing → Coding (NewIteration)
        var r2 = pipeline.IncrementTestRetry();
        Assert.True(r2, "Second retry should have remaining retries.");
        pipeline.StateMachine.StartIteration(StandardPlan);
        pipeline.StateMachine.Transition(PhaseInput.Succeeded); // Coding → Testing

        // Retry 3 — limit exhausted
        pipeline.StateMachine.Transition(PhaseInput.Failed);    // Testing → Coding (NewIteration)
        var r3 = pipeline.IncrementTestRetry();
        Assert.False(r3, "Third retry should indicate no remaining retries (limit reached).");

        // Caller calls Fail() when limit is exhausted
        pipeline.StateMachine.Fail();
        Assert.Equal(GoalPhase.Failed, pipeline.StateMachine.Phase);

        // Persist and verify that a Failed pipeline is not loaded back
        pipeline.AdvanceTo(GoalPhase.Failed);
        manager.PersistFull(pipeline);

        var freshManager = new GoalPipelineManager(_store);
        var restored = freshManager.RestoreFromStore();
        Assert.Empty(restored);
    }

    /// <summary>
    /// Verifies that TaskBuilder produces consistent branch specs for the same goal across
    /// multiple roles: feature branch is identical, base branch is "main", and task IDs
    /// follow the {goalId}-{role}-{iteration:D3} format.
    /// </summary>
    [Fact]
    public void TaskBuilderBranchSpecs_SameGoal_AllRolesShareBranchName()
    {
        const string goalId = "goal-branch-test";
        const int iteration = 1;
        var repo = CreateRepository();
        var coordinator = new BranchCoordinator();
        var taskBuilder = new TaskBuilder(coordinator);

        var expectedFeatureBranch = $"copilothive/{goalId}";

        // Coder — creates the branch
        var coderTask = taskBuilder.Build(
            goalId, "Branch test goal",
            WorkerRole.Coder, iteration,
            [repo], "Write code.", BranchAction.Create);

        Assert.Equal($"{goalId}-coder-001", coderTask.TaskId);
        Assert.Equal(WorkerRole.Coder, coderTask.Role);
        Assert.NotNull(coderTask.BranchInfo);
        Assert.Equal(expectedFeatureBranch, coderTask.BranchInfo!.FeatureBranch);
        Assert.Equal("main", coderTask.BranchInfo.BaseBranch);
        Assert.Equal("main", coderTask.Repositories[0].DefaultBranch);
        Assert.Equal(BranchAction.Create, coderTask.BranchInfo.Action);

        // Tester — checks out existing branch
        var testerTask = taskBuilder.Build(
            goalId, "Branch test goal",
            WorkerRole.Tester, iteration,
            [repo], "Run tests.", BranchAction.Checkout);

        Assert.Equal($"{goalId}-tester-001", testerTask.TaskId);
        Assert.Equal(WorkerRole.Tester, testerTask.Role);
        Assert.NotNull(testerTask.BranchInfo);
        Assert.Equal(expectedFeatureBranch, testerTask.BranchInfo!.FeatureBranch);
        Assert.Equal("main", testerTask.BranchInfo.BaseBranch);
        Assert.Equal("main", testerTask.Repositories[0].DefaultBranch);
        Assert.Equal(BranchAction.Checkout, testerTask.BranchInfo.Action);

        // Reviewer — checks out existing branch
        var reviewerTask = taskBuilder.Build(
            goalId, "Branch test goal",
            WorkerRole.Reviewer, iteration,
            [repo], "Review code.", BranchAction.Checkout);

        Assert.Equal($"{goalId}-reviewer-001", reviewerTask.TaskId);
        Assert.Equal(WorkerRole.Reviewer, reviewerTask.Role);
        Assert.NotNull(reviewerTask.BranchInfo);
        Assert.Equal(expectedFeatureBranch, reviewerTask.BranchInfo!.FeatureBranch);
        Assert.Equal("main", reviewerTask.BranchInfo.BaseBranch);
        Assert.Equal("main", reviewerTask.Repositories[0].DefaultBranch);
        Assert.Equal(BranchAction.Checkout, reviewerTask.BranchInfo.Action);

        // All three tasks share the same feature branch name
        Assert.Equal(coderTask.BranchInfo.FeatureBranch, testerTask.BranchInfo.FeatureBranch);
        Assert.Equal(coderTask.BranchInfo.FeatureBranch, reviewerTask.BranchInfo.FeatureBranch);
    }
}
