using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// End-to-end tests for the validate-and-reject planning contract: a planning failure is
/// surfaced as <see cref="PlanResult.Failed(string)"/> and propagated through every layer
/// until the goal itself fails. No layer substitutes <see cref="IterationPlan.Default"/>.
/// </summary>
public sealed class PlanRejectContractTests
{
    // ── ClarificationHandler.ResolvePlanAsync ───────────────────────────────

    [Fact]
    public async Task ResolvePlanAsync_NoBrain_ReturnsFailed()
    {
        var handler = new ClarificationHandler(
            brain: null, clarificationRouter: null, clarificationQueue: null,
            NullLogger<ClarificationHandler>.Instance);

        var result = await handler.ResolvePlanAsync(
            CreatePipeline(), null, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.Equal("no brain available", result.FailureReason);
        Assert.Null(result.Plan);
    }

    [Fact]
    public async Task ResolvePlanAsync_BrainSucceeds_ForwardsPlan()
    {
        var plan = IterationPlan.Default();
        var brain = new ScriptedPlanBrain(PlanResult.Success(plan));
        var handler = new ClarificationHandler(
            brain, clarificationRouter: null, clarificationQueue: null,
            NullLogger<ClarificationHandler>.Instance);

        var result = await handler.ResolvePlanAsync(
            CreatePipeline(), null, TestContext.Current.CancellationToken);

        Assert.False(result.IsFailed);
        Assert.Same(plan, result.Plan);
    }

    [Fact]
    public async Task ResolvePlanAsync_BrainFails_ForwardsFailure()
    {
        var brain = new ScriptedPlanBrain(PlanResult.Failed("brain exploded"));
        var handler = new ClarificationHandler(
            brain, clarificationRouter: null, clarificationQueue: null,
            NullLogger<ClarificationHandler>.Instance);

        var result = await handler.ResolvePlanAsync(
            CreatePipeline(), null, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.Equal("brain exploded", result.FailureReason);
        Assert.Null(result.Plan);
    }

    [Fact]
    public async Task ResolvePlanAsync_EscalationTimesOut_ReturnsFailed()
    {
        // No router and no queue → RouteEscalationAsync returns the timeout fallback message.
        var brain = new ScriptedPlanBrain(PlanResult.Escalated("Which branch?", "unclear"));
        var handler = new ClarificationHandler(
            brain, clarificationRouter: null, clarificationQueue: null,
            NullLogger<ClarificationHandler>.Instance);

        var result = await handler.ResolvePlanAsync(
            CreatePipeline(), null, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.Equal("planning clarification timed out", result.FailureReason);
        Assert.Null(result.Plan);
    }

    [Fact]
    public async Task ResolvePlanAsync_SecondConsecutiveEscalation_ReturnsFailed_AndIsNotRoutedAgain()
    {
        var queue = new ClarificationQueueService();
        var router = new AutoAnsweringRouter("Use the develop branch.");
        var brain = new ScriptedPlanBrain(
            PlanResult.Escalated("Which branch?", "unclear"),
            PlanResult.Escalated("Still unclear?", "still unclear"),
            PlanResult.Success(IterationPlan.Default()));
        var handler = new ClarificationHandler(
            brain, router, queue, NullLogger<ClarificationHandler>.Instance);

        var result = await handler.ResolvePlanAsync(
            CreatePipeline(), null, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.Equal("planning escalation loop", result.FailureReason);
        Assert.Null(result.Plan);

        // Bounded: exactly two planning calls, and only the FIRST escalation was routed.
        Assert.Equal(2, brain.PlanCallCount);
        Assert.Equal(1, router.RouteCount);
    }

    [Fact]
    public async Task ResolvePlanAsync_SuccessWithNullPlan_ReturnsFailed()
    {
        var brain = new ScriptedPlanBrain(NullPlanSuccess());
        var handler = new ClarificationHandler(
            brain, clarificationRouter: null, clarificationQueue: null,
            NullLogger<ClarificationHandler>.Instance);

        var result = await handler.ResolvePlanAsync(
            CreatePipeline(), null, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.Equal("brain returned success with no plan", result.FailureReason);
    }

    [Fact]
    public async Task ResolvePlanAsync_PostClarificationRetryFailed_ForwardsFailure()
    {
        // Brain escalates, clarification is auto-answered, then the retry returns Failed — not swallowed.
        var queue = new ClarificationQueueService();
        var router = new AutoAnsweringRouter("Use the develop branch.");
        var brain = new ScriptedPlanBrain(
            PlanResult.Escalated("Which branch?", "unclear"),
            PlanResult.Failed("brain exploded after clarification"));
        var handler = new ClarificationHandler(
            brain, router, queue, NullLogger<ClarificationHandler>.Instance);

        var result = await handler.ResolvePlanAsync(
            CreatePipeline(), null, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.Equal("brain exploded after clarification", result.FailureReason);
        Assert.Null(result.Plan);
        // The escalation was routed once, and the retry was the second planning call.
        Assert.Equal(2, brain.PlanCallCount);
        Assert.Equal(1, router.RouteCount);
    }

    [Fact]
    public async Task ResolvePlanAsync_PostClarificationRetrySuccessNullPlan_ReturnsFailed()
    {
        // Brain escalates, clarification is auto-answered, then the retry returns Success with Plan=null.
        var queue = new ClarificationQueueService();
        var router = new AutoAnsweringRouter("Use the develop branch.");
        var brain = new ScriptedPlanBrain(
            PlanResult.Escalated("Which branch?", "unclear"),
            NullPlanSuccess());
        var handler = new ClarificationHandler(
            brain, router, queue, NullLogger<ClarificationHandler>.Instance);

        var result = await handler.ResolvePlanAsync(
            CreatePipeline(), null, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.Equal("brain returned success with no plan after clarification", result.FailureReason);
        Assert.Null(result.Plan);
    }

    [Fact]
    public async Task ResolvePlanAsync_PostClarificationRetrySuccess_ForwardsPlan()
    {
        // Brain escalates, clarification is auto-answered, then the retry returns a valid plan.
        var queue = new ClarificationQueueService();
        var router = new AutoAnsweringRouter("Use the develop branch.");
        var plan = IterationPlan.Default();
        var brain = new ScriptedPlanBrain(
            PlanResult.Escalated("Which branch?", "unclear"),
            PlanResult.Success(plan));
        var handler = new ClarificationHandler(
            brain, router, queue, NullLogger<ClarificationHandler>.Instance);

        var result = await handler.ResolvePlanAsync(
            CreatePipeline(), null, TestContext.Current.CancellationToken);

        Assert.False(result.IsFailed);
        Assert.Same(plan, result.Plan);
    }

    // ── GoalDispatchService (new-goal dispatch) ─────────────────────────────

    [Fact]
    public async Task DispatchNextGoalAsync_PlanFails_FailsGoal_RemovesPipeline_DeletesSession()
    {
        var goal = new Goal
        {
            Id = "goal-plan-fail",
            Description = "Plan rejection test",
            Status = GoalStatus.Pending,
        };
        var goalStore = new PlanRejectRecordingGoalStore(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);

        var pipelineManager = new GoalPipelineManager();
        var brain = new ScriptedPlanBrain(PlanResult.Failed("plan grammar violated"));
        var taskQueue = new TaskQueue();
        var dispatchedTasks = new List<WorkTask>();
        taskQueue.OnEnqueue = t => { lock (dispatchedTasks) { dispatchedTasks.Add(t); } };

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            taskQueue,
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain: brain,
            startupDelay: TimeSpan.Zero);

        using var cts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var run = dispatcher.StartAsync(linked.Token);
        await WaitUntilAsync(
            () => goalStore.StatusUpdates.Any(u => u.Status == GoalStatus.Failed)
                && pipelineManager.GetByGoalId(goal.Id) is null
                && brain.DeletedSessions.Contains(goal.Id),
            TestContext.Current.CancellationToken);
        cts.Cancel();
        await Task.WhenAny(run, Task.Delay(1000, TestContext.Current.CancellationToken));

        // Goal is FAILED with the planning failure reason.
        var failures = goalStore.StatusUpdates.Where(u => u.Status == GoalStatus.Failed).ToList();
        Assert.Single(failures);
        Assert.Equal("plan grammar violated", failures[0].Metadata?.FailureReason);

        // Pipeline removed and the Brain goal session deleted.
        Assert.Null(pipelineManager.GetByGoalId(goal.Id));
        Assert.Contains(goal.Id, brain.DeletedSessions);

        // Nothing was dispatched to a worker.
        Assert.Empty(dispatchedTasks);
    }

    // ── GoalDispatcher.ResumeGoalAsync ──────────────────────────────────────

    [Fact]
    public async Task ResumeGoalAsync_PlanFails_FailsGoal()
    {
        var goal = new Goal
        {
            Id = "goal-resume-plan-fail",
            Description = "Resume plan rejection test",
            Status = GoalStatus.Failed,
            FailureReason = "Exceeded max iterations",
        };
        var goalStore = new PlanRejectRecordingGoalStore(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3, maxIterations: 1);
        while (pipeline.IterationBudget.TryConsume()) { }
        pipeline.AdvanceTo(GoalPhase.Failed);

        var brain = new ScriptedPlanBrain(PlanResult.Failed("resume plan rejected"));

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain: brain,
            goalStore: goalStore);

        var resumed = await dispatcher.ResumeGoalAsync(goal.Id, 5, TestContext.Current.CancellationToken);

        Assert.True(resumed);
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);

        var failures = goalStore.StatusUpdates.Where(u => u.Status == GoalStatus.Failed).ToList();
        Assert.Single(failures);
        Assert.Equal("resume plan rejected", failures[0].Metadata?.FailureReason);
    }

    // ── PipelineDriver replan sites ─────────────────────────────────────────

    [Fact]
    public async Task HandleNewIterationAsync_PlanFails_FailsGoal()
    {
        var (driver, pipeline, goalStore) = CreateDriver(PlanResult.Failed("iteration replan rejected"));

        await driver.HandleNewIterationAsync(pipeline, "FAIL", TestContext.Current.CancellationToken);

        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        var failures = goalStore.StatusUpdates.Where(u => u.Status == GoalStatus.Failed).ToList();
        Assert.Single(failures);
        Assert.Equal("iteration replan rejected", failures[0].Metadata?.FailureReason);
    }

    [Fact]
    public async Task HandleMergeFailureAsync_PlanFails_FailsGoal()
    {
        var (driver, pipeline, goalStore) = CreateDriver(PlanResult.Failed("merge replan rejected"));

        await driver.HandleMergeFailureAsync(pipeline, "conflict in Program.cs", TestContext.Current.CancellationToken);

        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        var failures = goalStore.StatusUpdates.Where(u => u.Status == GoalStatus.Failed).ToList();
        Assert.Single(failures);
        Assert.Equal("merge replan rejected", failures[0].Metadata?.FailureReason);
    }

    [Fact]
    public async Task HandleNewIterationAsync_PlanThrows_FailsGoalWithPlanningFailedReason()
    {
        var (driver, pipeline, goalStore) = CreateDriver(
            resolvePlan: (_, _, _) => throw new InvalidOperationException("brain socket closed"));

        await driver.HandleNewIterationAsync(pipeline, "FAIL", TestContext.Current.CancellationToken);

        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        Assert.Equal(GoalPhase.Failed, pipeline.StateMachine.Phase);
        var failures = goalStore.StatusUpdates.Where(u => u.Status == GoalStatus.Failed).ToList();
        Assert.Single(failures);
        Assert.Equal("Planning failed: brain socket closed", failures[0].Metadata?.FailureReason);
    }

    [Fact]
    public async Task HandleMergeFailureAsync_PlanThrows_FailsGoalWithPlanningFailedReason()
    {
        var (driver, pipeline, goalStore) = CreateDriver(
            resolvePlan: (_, _, _) => throw new InvalidOperationException("brain socket closed"));

        await driver.HandleMergeFailureAsync(pipeline, "conflict in Program.cs", TestContext.Current.CancellationToken);

        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        Assert.Equal(GoalPhase.Failed, pipeline.StateMachine.Phase);
        var failures = goalStore.StatusUpdates.Where(u => u.Status == GoalStatus.Failed).ToList();
        Assert.Single(failures);
        Assert.Equal("Planning failed: brain socket closed", failures[0].Metadata?.FailureReason);
    }

    [Fact]
    public async Task HandleNewIterationAsync_PlanCancelledByBrain_FailsGoalGracefully()
    {
        // The planning call cancels itself (Brain-side timeout) while the caller's token is live.
        var (driver, pipeline, goalStore) = CreateDriver(
            resolvePlan: (_, _, _) => throw new OperationCanceledException(new CancellationToken(canceled: true)));

        await driver.HandleNewIterationAsync(pipeline, "FAIL", TestContext.Current.CancellationToken);

        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        var failures = goalStore.StatusUpdates.Where(u => u.Status == GoalStatus.Failed).ToList();
        Assert.Single(failures);
        Assert.Equal("Planning failed: planning was cancelled", failures[0].Metadata?.FailureReason);
    }

    [Fact]
    public async Task HandleNewIterationAsync_CallerTokenCancelled_PropagatesOperationCanceled()
    {
        // Service shutdown: the caller's token is cancelled. The OperationCanceledException
        // MUST propagate — the goal must NOT be marked Failed (no spurious failure on shutdown).
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (driver, pipeline, goalStore) = CreateDriver(
            resolvePlan: (_, _, ct) => throw new OperationCanceledException(ct));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            driver.HandleNewIterationAsync(pipeline, "FAIL", cts.Token));

        // The goal was NOT marked Failed — cancellation is shutdown, not a planning failure.
        Assert.DoesNotContain(goalStore.StatusUpdates, u => u.Status == GoalStatus.Failed);
        Assert.NotEqual(GoalPhase.Failed, pipeline.Phase);
    }

    [Fact]
    public async Task HandleMergeFailureAsync_CallerTokenCancelled_PropagatesOperationCanceled()
    {
        // Same contract for the merge-failure replan site.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (driver, pipeline, goalStore) = CreateDriver(
            resolvePlan: (_, _, ct) => throw new OperationCanceledException(ct));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            driver.HandleMergeFailureAsync(pipeline, "conflict in Program.cs", cts.Token));

        Assert.DoesNotContain(goalStore.StatusUpdates, u => u.Status == GoalStatus.Failed);
        Assert.NotEqual(GoalPhase.Failed, pipeline.Phase);
    }

    [Fact]
    public async Task HandleNewIterationAsync_PlanStartsWithDocWriting_DispatchesDocWriterNotCoder()
    {
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.DocWriting, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging],
        };
        // Sanity: the plan must be grammar-valid, otherwise the assertion below proves nothing.
        Assert.True(IterationPlanValidator.ValidatePlanStrict(plan).IsValid);

        var dispatchedRoles = new List<WorkerRole>();
        var promptedPhases = new List<GoalPhase>();
        var (driver, pipeline, _) = CreateDriver(
            resolvePlan: (_, _, _) => Task.FromResult(PlanResult.Success(plan)),
            dispatchToRole: (_, role, _, _) => { dispatchedRoles.Add(role); return Task.CompletedTask; },
            resolvePrompt: (_, phase, _, _) => { promptedPhases.Add(phase); return Task.FromResult("prompt"); });

        await driver.HandleNewIterationAsync(pipeline, "FAIL", TestContext.Current.CancellationToken);

        // The plan's FIRST phase drives the pipeline — not a hardcoded Coding phase.
        Assert.Equal(GoalPhase.DocWriting, pipeline.Phase);
        Assert.Equal([WorkerRole.DocWriter], dispatchedRoles);
        Assert.Equal([GoalPhase.DocWriting], promptedPhases);
        Assert.Equal(GoalPhase.DocWriting, pipeline.PhaseLog[^1].Name);
    }

    [Fact]
    public async Task HandleMergeFailureAsync_PlanStartsWithDocWriting_DispatchesDocWriterNotCoder()
    {
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.DocWriting, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging],
        };

        var dispatchedRoles = new List<WorkerRole>();
        var (driver, pipeline, _) = CreateDriver(
            resolvePlan: (_, _, _) => Task.FromResult(PlanResult.Success(plan)),
            dispatchToRole: (_, role, _, _) => { dispatchedRoles.Add(role); return Task.CompletedTask; });

        await driver.HandleMergeFailureAsync(pipeline, "conflict in Program.cs", TestContext.Current.CancellationToken);

        Assert.Equal(GoalPhase.DocWriting, pipeline.Phase);
        Assert.Equal([WorkerRole.DocWriter], dispatchedRoles);
    }

    // ── Throwing planning at the dispatcher layers ──────────────────────────

    [Fact]
    public async Task DispatchNextGoalAsync_PlanThrows_FailsGoal_AndCleansUp()
    {
        var goal = new Goal
        {
            Id = "goal-plan-throw",
            Description = "Plan throw test",
            Status = GoalStatus.Pending,
        };
        var goalStore = new PlanRejectRecordingGoalStore(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);

        var pipelineManager = new GoalPipelineManager();
        var brain = new ThrowingPlanBrain(new InvalidOperationException("brain socket closed"));
        var taskQueue = new TaskQueue();
        var dispatchedTasks = new List<WorkTask>();
        taskQueue.OnEnqueue = t => { lock (dispatchedTasks) { dispatchedTasks.Add(t); } };

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            taskQueue,
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain: brain,
            startupDelay: TimeSpan.Zero);

        using var cts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var run = dispatcher.StartAsync(linked.Token);
        await WaitUntilAsync(
            () => goalStore.StatusUpdates.Any(u => u.Status == GoalStatus.Failed)
                && pipelineManager.GetByGoalId(goal.Id) is null
                && brain.DeletedSessions.Contains(goal.Id),
            TestContext.Current.CancellationToken);
        cts.Cancel();
        await Task.WhenAny(run, Task.Delay(1000, TestContext.Current.CancellationToken));

        var failures = goalStore.StatusUpdates.Where(u => u.Status == GoalStatus.Failed).ToList();
        Assert.Single(failures);
        Assert.StartsWith("Planning failed:", failures[0].Metadata?.FailureReason);

        // Cleanup still ran: pipeline removed, Brain session deleted, nothing dispatched.
        Assert.Null(pipelineManager.GetByGoalId(goal.Id));
        Assert.Contains(goal.Id, brain.DeletedSessions);
        Assert.Empty(dispatchedTasks);
    }

    [Fact]
    public async Task DispatchNextGoalAsync_PlanFails_CleanupThrows_GoalStillMarkedFailed()
    {
        var goal = new Goal
        {
            Id = "goal-plan-fail-cleanup-throws",
            Description = "Failure-safe cleanup test",
            Status = GoalStatus.Pending,
        };
        var goalStore = new PlanRejectRecordingGoalStore(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);

        var pipelineManager = new GoalPipelineManager();
        // Brain fails planning AND throws while deleting the goal session.
        var brain = new ScriptedPlanBrain(PlanResult.Failed("plan grammar violated"))
        {
            DeleteSessionException = new InvalidOperationException("session store offline"),
        };

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain: brain,
            startupDelay: TimeSpan.Zero);

        using var cts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var run = dispatcher.StartAsync(linked.Token);
        await WaitUntilAsync(
            () => goalStore.StatusUpdates.Any(u => u.Status == GoalStatus.Failed)
                && pipelineManager.GetByGoalId(goal.Id) is null,
            TestContext.Current.CancellationToken);
        cts.Cancel();
        await Task.WhenAny(run, Task.Delay(1000, TestContext.Current.CancellationToken));

        // The DB is the source of truth — the goal is Failed even though cleanup threw.
        var failures = goalStore.StatusUpdates.Where(u => u.Status == GoalStatus.Failed).ToList();
        Assert.Single(failures);
        Assert.Equal("plan grammar violated", failures[0].Metadata?.FailureReason);

        // Cleanup steps AFTER the throwing one still ran.
        Assert.Null(pipelineManager.GetByGoalId(goal.Id));
    }

    [Fact]
    public async Task DispatchNextGoalAsync_PlanFails_StoreThrows_CleanupStillRuns()
    {
        // Step 1 (UpdateGoalStatusAsync) throws — steps 2-4 (deregister, remove pipeline, delete session)
        // must STILL run so no runtime state is left dangling.
        var goal = new Goal
        {
            Id = "goal-plan-fail-store-throws",
            Description = "Store-throws cleanup test",
            Status = GoalStatus.Pending,
        };
        var goalStore = new ThrowingUpdateGoalStore(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);

        var pipelineManager = new GoalPipelineManager();
        var brain = new ScriptedPlanBrain(PlanResult.Failed("plan grammar violated"));

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain: brain,
            startupDelay: TimeSpan.Zero);

        using var cts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var run = dispatcher.StartAsync(linked.Token);
        await WaitUntilAsync(
            () => pipelineManager.GetByGoalId(goal.Id) is null
                && brain.DeletedSessions.Contains(goal.Id),
            TestContext.Current.CancellationToken);
        cts.Cancel();
        await Task.WhenAny(run, Task.Delay(1000, TestContext.Current.CancellationToken));

        // The store threw, but cleanup steps STILL ran independently.
        Assert.Null(pipelineManager.GetByGoalId(goal.Id));  // pipeline removed
        Assert.Contains(goal.Id, brain.DeletedSessions);     // brain session deleted
    }

    [Fact]
    public async Task ResumeGoalAsync_PlanThrows_FailsGoal_AndPersistsTerminalState()
    {
        var goal = new Goal
        {
            Id = "goal-resume-plan-throw",
            Description = "Resume plan throw test",
            Status = GoalStatus.Failed,
            FailureReason = "Exceeded max iterations",
        };
        var goalStore = new PlanRejectRecordingGoalStore(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3, maxIterations: 1);
        while (pipeline.IterationBudget.TryConsume()) { }
        pipeline.AdvanceTo(GoalPhase.Failed);

        var brain = new ThrowingPlanBrain(new InvalidOperationException("brain socket closed"));

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain: brain,
            goalStore: goalStore);

        var resumed = await dispatcher.ResumeGoalAsync(goal.Id, 5, TestContext.Current.CancellationToken);

        Assert.True(resumed);
        // Terminal state is synchronized across the pipeline phase and the state machine.
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        Assert.Equal(GoalPhase.Failed, pipeline.StateMachine.Phase);

        var failures = goalStore.StatusUpdates.Where(u => u.Status == GoalStatus.Failed).ToList();
        Assert.Single(failures);
        Assert.Equal("Planning failed: brain socket closed", failures[0].Metadata?.FailureReason);
    }

    [Fact]
    public async Task ResumeGoalAsync_PlanCancelledByBrain_FailsGoalGracefully()
    {
        var goal = new Goal
        {
            Id = "goal-resume-plan-cancel",
            Description = "Resume plan cancellation test",
            Status = GoalStatus.Failed,
            FailureReason = "Exceeded max iterations",
        };
        var goalStore = new PlanRejectRecordingGoalStore(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3, maxIterations: 1);
        while (pipeline.IterationBudget.TryConsume()) { }
        pipeline.AdvanceTo(GoalPhase.Failed);

        var brain = new ThrowingPlanBrain(
            new OperationCanceledException(new CancellationToken(canceled: true)));

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain: brain,
            goalStore: goalStore);

        var resumed = await dispatcher.ResumeGoalAsync(goal.Id, 5, TestContext.Current.CancellationToken);

        Assert.True(resumed);
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);

        var failures = goalStore.StatusUpdates.Where(u => u.Status == GoalStatus.Failed).ToList();
        Assert.Single(failures);
        Assert.Equal("Planning failed: planning was cancelled", failures[0].Metadata?.FailureReason);
    }

    [Fact]
    public async Task ResumeGoalAsync_PlanFails_PersistsTerminalFailedState()
    {
        // The resume failure path must call PersistFull AFTER MarkGoalFailedAsync so the
        // durable pipeline state is Failed, not Planning. We verify by loading the
        // pipeline snapshot from a real in-memory PipelineStore.
        var goal = new Goal
        {
            Id = "goal-resume-persist",
            Description = "Resume persistence test",
            Status = GoalStatus.Failed,
            FailureReason = "Exceeded max iterations",
        };
        var goalStore = new PlanRejectRecordingGoalStore(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);

        var dbContext = CopilotHive.Persistence.CopilotHiveDbContext.CreateInMemory();
        var pipelineStore = new CopilotHive.Persistence.PipelineStore(
            dbContext, NullLogger<CopilotHive.Persistence.PipelineStore>.Instance);
        var pipelineManager = new GoalPipelineManager(pipelineStore);
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3, maxIterations: 1);
        while (pipeline.IterationBudget.TryConsume()) { }
        pipeline.AdvanceTo(GoalPhase.Failed);

        var brain = new ScriptedPlanBrain(PlanResult.Failed("persist-reason-test"));

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain: brain,
            goalStore: goalStore);

        var resumed = await dispatcher.ResumeGoalAsync(goal.Id, 5, TestContext.Current.CancellationToken);

        Assert.True(resumed);
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        Assert.Equal(GoalPhase.Failed, pipeline.StateMachine.Phase);

        // The durable store reflects the terminal Failed state — not Planning.
        var snapshot = pipelineStore.LoadPipeline(goal.Id);
        Assert.NotNull(snapshot);
        Assert.Equal(GoalPhase.Failed, snapshot!.Phase);

        var failures = goalStore.StatusUpdates.Where(u => u.Status == GoalStatus.Failed).ToList();
        Assert.Single(failures);
        Assert.Equal("persist-reason-test", failures[0].Metadata?.FailureReason);
    }

    [Fact]
    public async Task ResumeGoalAsync_PlanThrows_PersistsTerminalFailedState()
    {
        // Even when MarkGoalFailedAsync throws (or completes), PersistFull runs in a finally
        // so the pipeline is durably persisted as Failed. The throw path must also persist.
        var goal = new Goal
        {
            Id = "goal-resume-persist-throw",
            Description = "Resume persistence throw test",
            Status = GoalStatus.Failed,
            FailureReason = "Exceeded max iterations",
        };
        var goalStore = new PlanRejectRecordingGoalStore(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);

        var dbContext = CopilotHive.Persistence.CopilotHiveDbContext.CreateInMemory();
        var pipelineStore = new CopilotHive.Persistence.PipelineStore(
            dbContext, NullLogger<CopilotHive.Persistence.PipelineStore>.Instance);
        var pipelineManager = new GoalPipelineManager(pipelineStore);
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3, maxIterations: 1);
        while (pipeline.IterationBudget.TryConsume()) { }
        pipeline.AdvanceTo(GoalPhase.Failed);

        var brain = new ThrowingPlanBrain(new InvalidOperationException("brain socket closed"));

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain: brain,
            goalStore: goalStore);

        var resumed = await dispatcher.ResumeGoalAsync(goal.Id, 5, TestContext.Current.CancellationToken);

        Assert.True(resumed);
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);

        // The durable store reflects the terminal Failed state.
        var snapshot = pipelineStore.LoadPipeline(goal.Id);
        Assert.NotNull(snapshot);
        Assert.Equal(GoalPhase.Failed, snapshot!.Phase);
    }

    [Fact]
    public async Task ResumeGoalAsync_PlanFails_DoesNotDoubleAdvance_CompletedAtSetOnce()
    {
        // FailResumedGoalAsync must NOT call AdvanceTo(Failed) in its finally —
        // MarkGoalFailedAsync owns that transition. A second AdvanceTo would rewrite
        // CompletedAt after the lifecycle metadata was already written, leaving the goal
        // and the persisted pipeline with mismatched timestamps.
        var goal = new Goal
        {
            Id = "goal-resume-no-double-advance",
            Description = "Resume double-advance test",
            Status = GoalStatus.Failed,
            FailureReason = "Exceeded max iterations",
        };
        var goalStore = new PlanRejectRecordingGoalStore(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3, maxIterations: 1);
        while (pipeline.IterationBudget.TryConsume()) { }
        pipeline.AdvanceTo(GoalPhase.Failed);
        // Clear CompletedAt so we can detect if it's set exactly once by MarkGoalFailedAsync.
        pipeline.ClearCompletedAt();
        Assert.Null(pipeline.CompletedAt);

        var brain = new ScriptedPlanBrain(PlanResult.Failed("double-advance-reason"));

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain: brain,
            goalStore: goalStore);

        var resumed = await dispatcher.ResumeGoalAsync(goal.Id, 5, TestContext.Current.CancellationToken);

        Assert.True(resumed);
        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        Assert.Equal(GoalPhase.Failed, pipeline.StateMachine.Phase);

        // CompletedAt was set exactly once by MarkGoalFailedAsync → AdvanceTo(Failed).
        // If FailResumedGoalAsync also called AdvanceTo, CompletedAt would have been
        // overwritten. We verify it is set (not null) and that only ONE Failed status
        // update was recorded (proving MarkGoalFailedAsync ran exactly once).
        Assert.NotNull(pipeline.CompletedAt);
        var failures = goalStore.StatusUpdates.Where(u => u.Status == GoalStatus.Failed).ToList();
        Assert.Single(failures);
        Assert.Equal("double-advance-reason", failures[0].Metadata?.FailureReason);

        // The CompletedAt in the goal metadata matches the pipeline's CompletedAt —
        // no second advance rewrote it after FinalizeGoalAsync read it.
        Assert.Equal(pipeline.CompletedAt, failures[0].Metadata?.CompletedAt);
    }

    // ── PlanIterationAsync never throws (pre-connect included) ──────────────

    [Fact]
    public async Task PlanIterationAsync_NotConnected_ReturnsFailed_DoesNotThrow()
    {
        // EnsureConnected lives INSIDE the try: PlanIterationAsync must never throw a planning
        // error at its callers — even pre-connect misuse surfaces as PlanResult.Failed so the
        // goal fails with an explicit reason instead of silently receiving a default plan.
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);

        var result = await brain.PlanIterationAsync(
            CreatePipeline(), null, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.False(result.IsEscalation);
        Assert.Null(result.Plan);
        Assert.StartsWith("Planning failed:", result.FailureReason);
    }

    // ── TaskCompletionService cancellation propagation ──────────────────────

    [Fact]
    public async Task HandleTaskCompletionAsync_CallerTokenCancelled_PropagatesAndDoesNotFailGoal()
    {
        // End-to-end: PipelineDriver rethrows caller cancellation from the replan site, and
        // TaskCompletionService must NOT convert it into MarkGoalFailedAsync with an
        // already-cancelled token (which would mutate the pipeline to Failed and then fail
        // to persist it).
        var goal = new Goal { Id = "goal-tcs-cancel", Description = "TCS cancellation test" };
        var goalStore = new PlanRejectRecordingGoalStore(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3, maxIterations: 5);
        var plan = IterationPlan.Default();
        pipeline.SetPlan(plan);
        // Sit on Review: a FAIL there drives NewIteration → replan (Coding would instead hit the
        // separate no-op-coder retry path, which never replans).
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Review);
        pipeline.AdvanceTo(GoalPhase.Review);
        pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Review, pipeline.Iteration, 1));

        var taskId = $"task-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);
        pipeline.SetActiveTask(taskId);

        var lifecycleService = new GoalLifecycleService(goalManager, NullLogger<GoalLifecycleService>.Instance);
        var brain = new ScriptedPlanBrain(PlanResult.Failed("unused"));

        // Planning honours the caller's token: a cancelled caller yields an OCE carrying that token.
        var driver = new PipelineDriver(
            brain: brain,
            lifecycleService: lifecycleService,
            goalManager: goalManager,
            repoManager: new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            improvementAnalyzer: null,
            agentsManager: null,
            metricsTracker: null,
            dispatchToRole: (_, _, _, _) => Task.CompletedTask,
            resolvePrompt: (_, _, _, _) => Task.FromResult("prompt"),
            resolvePlan: (_, _, planCt) => throw new OperationCanceledException(planCt),
            resolveRepositories: _ => [],
            syncAgents: _ => Task.CompletedTask,
            generateMergeCommitMessage: (_, _) => Task.FromResult("message"),
            logger: NullLogger<PipelineDriver>.Instance);

        var service = new TaskCompletionService(
            pipelineManager, brain, driver, lifecycleService,
            dashboardNotifier: null, NullLogger<TaskCompletionService>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // A FAIL verdict on Review drives a NewIteration → replan → caller-cancelled OCE.
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.HandleTaskCompletionAsync(
                new TaskResult
                {
                    TaskId = taskId,
                    Status = TaskOutcome.Completed,
                    Output = "reviewer finished",
                    Metrics = new TaskMetrics { Verdict = "FAIL" },
                },
                cts.Token));

        // The goal must NOT be Failed — cancellation is shutdown, not a pipeline failure.
        Assert.DoesNotContain(goalStore.StatusUpdates, u => u.Status == GoalStatus.Failed);
        Assert.NotEqual(GoalPhase.Failed, pipeline.Phase);
    }

    [Fact]
    public async Task HandleTaskCompletionAsync_MergeFailureReplanCancelled_PropagatesAndDoesNotFailGoal()
    {
        // Same OCE-propagation contract as the iteration replan, but through the merge-failure
        // path: DriveNextPhaseAsync processes a Review PASS → transitions to Merging →
        // DispatchPhaseAsync(Merging) → PerformMergeAsync throws → HandleMergeFailureAsync
        // → replan → caller-token cancelled → OCE propagates through TaskCompletionService.
        var goal = new Goal { Id = "goal-tcs-merge-cancel", Description = "TCS merge cancellation test" };
        var goalStore = new PlanRejectRecordingGoalStore(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3, maxIterations: 5);
        var plan = IterationPlan.Default();
        pipeline.SetPlan(plan);
        // Sit on Review: a PASS verdict transitions to Merging, which dispatches PerformMergeAsync.
        pipeline.StateMachine.RestoreFromPlan(plan.Phases, GoalPhase.Review);
        pipeline.AdvanceTo(GoalPhase.Review);
        pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Review, pipeline.Iteration, 1));
        // Set CoderBranch so PerformMergeAsync doesn't bail with "No coder branch set".
        pipeline.SetActiveTask($"prev-task-{Guid.NewGuid():N}", "feature-branch");

        var taskId = $"task-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);
        pipeline.SetActiveTask(taskId, "feature-branch");

        var lifecycleService = new GoalLifecycleService(goalManager, NullLogger<GoalLifecycleService>.Instance);
        var brain = new ScriptedPlanBrain(PlanResult.Failed("unused"));

        // resolvePlan throws OCE with the caller's token — proving the merge-failure replan
        // path propagates cancellation through TaskCompletionService.
        var driver = new PipelineDriver(
            brain: brain,
            lifecycleService: lifecycleService,
            goalManager: goalManager,
            repoManager: new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            improvementAnalyzer: null,
            agentsManager: null,
            metricsTracker: null,
            dispatchToRole: (_, _, _, _) => Task.CompletedTask,
            resolvePrompt: (_, _, _, _) => Task.FromResult("prompt"),
            resolvePlan: (_, _, planCt) => throw new OperationCanceledException(planCt),
            resolveRepositories: _ => [],
            syncAgents: _ => Task.CompletedTask,
            // generateMergeCommitMessage throws to force the merge-failure path
            // (PerformMergeAsync catch → HandleMergeFailureAsync → replan → OCE).
            generateMergeCommitMessage: (_, _) => throw new InvalidOperationException("merge commit gen failed"),
            logger: NullLogger<PipelineDriver>.Instance);

        var service = new TaskCompletionService(
            pipelineManager, brain, driver, lifecycleService,
            dashboardNotifier: null, NullLogger<TaskCompletionService>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // A PASS verdict on Review → transition to Merging → PerformMergeAsync throws
        // → HandleMergeFailureAsync → replan → caller-cancelled OCE propagates through TCS.
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.HandleTaskCompletionAsync(
                new TaskResult
                {
                    TaskId = taskId,
                    Status = TaskOutcome.Completed,
                    Output = "review passed",
                    Metrics = new TaskMetrics { Verdict = "PASS" },
                },
                cts.Token));

        // The goal must NOT be Failed — cancellation is shutdown, not a pipeline failure.
        Assert.DoesNotContain(goalStore.StatusUpdates, u => u.Status == GoalStatus.Failed);
        Assert.NotEqual(GoalPhase.Failed, pipeline.Phase);
    }

    // ── Cancellation-safe cleanup on the new-goal path ──────────────────────

    [Fact]
    public async Task DispatchNextGoalAsync_PlanFails_CallerTokenCancelled_StillFailsGoalAndCleansUp()
    {
        // The caller's token is cancelled while planning fails. Cleanup must use
        // CancellationToken.None so the goal status write completes — otherwise the pipeline
        // is deleted while a persisted InProgress goal is left behind with no pipeline.
        var goal = new Goal
        {
            Id = "goal-cancel-cleanup",
            Description = "Cancellation-safe cleanup test",
            Status = GoalStatus.Pending,
        };
        var goalStore = new CancellationAwareGoalStore(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);

        var pipelineManager = new GoalPipelineManager();
        // Planning fails AND cancels the caller's token, so the failure path runs under cancellation.
        using var cts = new CancellationTokenSource();
        var brain = new CancellingOnPlanBrain(cts, PlanResult.Failed("plan grammar violated"));

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain: brain,
            startupDelay: TimeSpan.Zero);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        await dispatcher.StartAsync(linked.Token);

        // Wait for all asserted cleanup state to complete: Failed status persistence,
        // pipeline removal, and session deletion. DeleteGoalSessionAsync is the last
        // state mutation asserted by this test, so its completion means all assertions
        // can be evaluated safely.
        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var cleanupLinked = CancellationTokenSource.CreateLinkedTokenSource(
            cleanupTimeout.Token, TestContext.Current.CancellationToken);
        try
        {
            await brain.CleanupCompleted.Task.WaitAsync(cleanupLinked.Token);
        }
        catch (OperationCanceledException)
        {
            // Timeout or test cancellation — fall through to assertions which will fail with clear messages.
        }

        // Every cleanup step ran to completion despite the cancelled caller token.
        var failures = goalStore.StatusUpdates.Where(u => u.Status == GoalStatus.Failed).ToList();
        Assert.Single(failures);
        Assert.Equal("plan grammar violated", failures[0].Metadata?.FailureReason);
        Assert.Null(pipelineManager.GetByGoalId(goal.Id));
        Assert.Contains(goal.Id, brain.DeletedSessions);

        // The cleanup writes used CancellationToken.None — not merely an uncancelled token, but a
        // token that CANNOT be cancelled. Asserting IsCancellationRequested would be meaningless
        // here: CancellationToken is a struct over a live source, so a caller token captured
        // earlier would report cancellation once the source is cancelled.
        var failedUpdateToken = goalStore.StatusUpdateTokens[goalStore.StatusUpdates.FindIndex(
            u => u.Status == GoalStatus.Failed)];
        Assert.False(failedUpdateToken.CanBeCanceled);
        Assert.All(brain.DeleteSessionTokens, t => Assert.False(t.CanBeCanceled));
    }

    // ── Merge-failure retry logs its phase ──────────────────────────────────

    [Fact]
    public async Task HandleMergeFailureAsync_ValidPlan_AddsPhaseLogEntryWithPrompt()
    {
        // Without a fresh PhaseLog entry, CurrentPhaseEntry would still be the prior Merging
        // entry and the retry worker's output/verdict would overwrite merge history.
        var plan = IterationPlan.Default();
        var (driver, pipeline, _) = CreateDriver(
            resolvePlan: (_, _, _) => Task.FromResult(PlanResult.Success(plan)),
            resolvePrompt: (_, _, _, _) => Task.FromResult("rebase and fix the conflict"));

        // Simulate the completed Merging phase that precedes a merge failure.
        pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Merging, pipeline.Iteration, 1));
        var mergingEntry = pipeline.CurrentPhaseEntry!;
        mergingEntry.WorkerOutput = "merge attempt output";
        var iterationBefore = pipeline.Iteration;

        await driver.HandleMergeFailureAsync(
            pipeline, "conflict in Program.cs", TestContext.Current.CancellationToken);

        var firstPhase = plan.Phases[0];
        Assert.Equal(firstPhase, pipeline.Phase);

        // A new entry exists for the retry iteration's first phase and is now the current entry.
        var newEntry = pipeline.CurrentPhaseEntry;
        Assert.NotNull(newEntry);
        Assert.NotSame(mergingEntry, newEntry);
        Assert.Equal(firstPhase, newEntry!.Name);
        Assert.Equal(pipeline.Iteration, newEntry.Iteration);
        Assert.Equal("rebase and fix the conflict", newEntry.WorkerPrompt);

        // The prior Merging entry is marked failed with the merge error so the
        // iteration summary (and dashboard) shows the failed Merging phase.
        Assert.Equal(PhaseOutcome.Fail, mergingEntry.Result);
        Assert.Equal("conflict in Program.cs", mergingEntry.WorkerOutput);
        Assert.NotNull(mergingEntry.CompletedAt);
        Assert.Equal(iterationBefore, mergingEntry.Iteration);
    }

    // ── No assumed Coding phase before planning ─────────────────────────────

    [Fact]
    public async Task HandleNewIterationAsync_PlanningObservesPrePlanningPhase_NotAssumedCoding()
    {
        // The pipeline must NOT be advanced to an assumed Coding phase before planning:
        // planning would observe a phase the Brain never chose.
        var observedPhases = new List<GoalPhase>();
        var plan = IterationPlan.Default();
        var (driver, pipeline, _) = CreateDriver(
            resolvePlan: (p, _, _) => { observedPhases.Add(p.Phase); return Task.FromResult(PlanResult.Success(plan)); });

        // CreateDriver leaves the pipeline in Review (the phase that triggered the new iteration).
        Assert.Equal(GoalPhase.Review, pipeline.Phase);

        await driver.HandleNewIterationAsync(pipeline, "FAIL", TestContext.Current.CancellationToken);

        Assert.Equal([GoalPhase.Review], observedPhases);
        // Only AFTER a valid plan is accepted does the phase move to the planned first phase.
        Assert.Equal(plan.Phases[0], pipeline.Phase);
    }

    [Fact]
    public async Task HandleNewIterationAsync_CallerCancelled_LeavesNoTransientCodingPhase()
    {
        // Caller cancellation during planning must not leave a transient Coding assumption behind.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (driver, pipeline, _) = CreateDriver(
            resolvePlan: (_, _, ct) => throw new OperationCanceledException(ct));

        Assert.Equal(GoalPhase.Review, pipeline.Phase);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            driver.HandleNewIterationAsync(pipeline, "FAIL", cts.Token));

        // The pipeline stays in its pre-planning phase — no assumed Coding transition survived.
        Assert.Equal(GoalPhase.Review, pipeline.Phase);
    }

    [Fact]
    public async Task HandleMergeFailureAsync_CallerCancelled_LeavesNoTransientCodingPhase()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (driver, pipeline, _) = CreateDriver(
            resolvePlan: (_, _, ct) => throw new OperationCanceledException(ct));

        Assert.Equal(GoalPhase.Review, pipeline.Phase);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            driver.HandleMergeFailureAsync(pipeline, "conflict in Program.cs", cts.Token));

        Assert.Equal(GoalPhase.Review, pipeline.Phase);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Polls <paramref name="condition"/> until it becomes true instead of sleeping a fixed
    /// duration. <c>GoalDispatcher.StartAsync</c> (inherited from <c>BackgroundService</c>)
    /// returns as soon as the loop has been kicked off, not once it has actually finished a
    /// dispatch cycle — so the returned task cannot be awaited to know when cleanup has run.
    /// A fixed <c>Task.Delay</c> before asserting is inherently racy under
    /// CPU contention (e.g. higher xUnit parallelism): the delay may not be long enough on a
    /// loaded machine, causing intermittent false failures. Polling for the actual observable
    /// state removes that race on any system, timezone, or filesystem while still failing fast
    /// (via <see cref="TimeoutException"/>) if the condition is never met, which would indicate
    /// a real hang rather than a flake.
    /// </summary>
    private static async Task WaitUntilAsync(
        Func<bool> condition, CancellationToken ct, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Condition was not met within the timeout.");

            await Task.Delay(10, ct);
        }
    }

    private static GoalPipeline CreatePipeline()
    {
        var pipeline = new GoalPipeline(new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" });
        pipeline.AdvanceTo(GoalPhase.Planning);
        return pipeline;
    }

    /// <summary>Builds a <see cref="PlanResult"/> that reports success but carries no plan.</summary>
    private static PlanResult NullPlanSuccess() => new();

    private static (PipelineDriver Driver, GoalPipeline Pipeline, PlanRejectRecordingGoalStore Store) CreateDriver(
        PlanResult planResult)
        => CreateDriver(resolvePlan: (_, _, _) => Task.FromResult(planResult));

    private static (PipelineDriver Driver, GoalPipeline Pipeline, PlanRejectRecordingGoalStore Store) CreateDriver(
        Func<GoalPipeline, string?, CancellationToken, Task<PlanResult>> resolvePlan,
        Func<GoalPipeline, WorkerRole, string?, CancellationToken, Task>? dispatchToRole = null,
        Func<GoalPipeline, GoalPhase, string?, CancellationToken, Task<string>>? resolvePrompt = null)
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Driver replan test" };
        var goalStore = new PlanRejectRecordingGoalStore(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalStore);

        var pipeline = new GoalPipelineManager().CreatePipeline(goal, maxRetries: 3, maxIterations: 5);
        pipeline.AdvanceTo(GoalPhase.Review);

        var lifecycleService = new GoalLifecycleService(goalManager, NullLogger<GoalLifecycleService>.Instance);

        var driver = new PipelineDriver(
            brain: new ScriptedPlanBrain(PlanResult.Failed("unused")),
            lifecycleService: lifecycleService,
            goalManager: goalManager,
            repoManager: new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            improvementAnalyzer: null,
            agentsManager: null,
            metricsTracker: null,
            dispatchToRole: dispatchToRole ?? ((_, _, _, _) => Task.CompletedTask),
            resolvePrompt: resolvePrompt ?? ((_, _, _, _) => Task.FromResult("prompt")),
            resolvePlan: resolvePlan,
            resolveRepositories: _ => [],
            syncAgents: _ => Task.CompletedTask,
            generateMergeCommitMessage: (_, _) => Task.FromResult("message"),
            logger: NullLogger<PipelineDriver>.Instance);

        return (driver, pipeline, goalStore);
    }
}

/// <summary>
/// <see cref="IDistributedBrain"/> that replays a scripted sequence of
/// <see cref="PlanResult"/> values (the last one repeats once exhausted).
/// </summary>
file sealed class ScriptedPlanBrain : IDistributedBrain
{
    private readonly PlanResult[] _results;
    private int _planCallCount;

    internal ScriptedPlanBrain(params PlanResult[] results) => _results = results;

    /// <summary>Number of <see cref="PlanIterationAsync"/> invocations.</summary>
    internal int PlanCallCount => _planCallCount;

    /// <summary>Goal IDs whose Brain session was deleted.</summary>
    internal List<string> DeletedSessions { get; } = [];

    /// <summary>When set, <see cref="DeleteGoalSessionAsync"/> throws this exception.</summary>
    internal Exception? DeleteSessionException { get; init; }

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<PlanResult> PlanIterationAsync(
        GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default)
    {
        var call = Interlocked.Increment(ref _planCallCount);
        return Task.FromResult(_results[Math.Min(call - 1, _results.Length - 1)]);
    }

    public Task<PromptResult> CraftPromptAsync(
        GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
        Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));

    public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task EnsureBrainRepoAsync(
        string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<BrainResponse> AskQuestionAsync(
        string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
        Task.FromResult(BrainResponse.Answer("proceed"));

    public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default)
    {
        if (DeleteSessionException is not null)
            return Task.FromException(DeleteSessionException);

        DeletedSessions.Add(goalId);
        return Task.CompletedTask;
    }

    public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public bool GoalSessionExists(string goalId) => false;

    public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

    public BrainStats? GetStats() => null;
}

/// <summary>
/// <see cref="IDistributedBrain"/> whose <see cref="PlanIterationAsync"/> always throws the
/// configured exception — used to prove that thrown planning paths fail the goal.
/// </summary>
file sealed class ThrowingPlanBrain : IDistributedBrain
{
    private readonly Exception _exception;

    internal ThrowingPlanBrain(Exception exception) => _exception = exception;

    /// <summary>Goal IDs whose Brain session was deleted.</summary>
    internal List<string> DeletedSessions { get; } = [];

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<PlanResult> PlanIterationAsync(
        GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
        Task.FromException<PlanResult>(_exception);

    public Task<PromptResult> CraftPromptAsync(
        GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
        Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));

    public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task EnsureBrainRepoAsync(
        string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<BrainResponse> AskQuestionAsync(
        string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
        Task.FromResult(BrainResponse.Answer("proceed"));

    public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default)
    {
        DeletedSessions.Add(goalId);
        return Task.CompletedTask;
    }

    public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public bool GoalSessionExists(string goalId) => false;

    public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

    public BrainStats? GetStats() => null;
}

/// <summary>Clarification router that always auto-answers and records how often it was used.</summary>
file sealed class AutoAnsweringRouter : IClarificationRouter
{
    private readonly string _answer;
    private int _routeCount;

    internal AutoAnsweringRouter(string answer) => _answer = answer;

    /// <summary>Number of clarification requests routed through this router.</summary>
    internal int RouteCount => _routeCount;

    public Task<string?> TryAutoAnswerAsync(
        string goalId,
        string question,
        string context,
        ClarificationQueueService clarificationQueue,
        ClarificationRequest request,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _routeCount);
        return Task.FromResult<string?>(_answer);
    }
}

/// <summary>
/// <see cref="IDistributedBrain"/> that cancels the supplied token when planning is requested and
/// then returns the scripted failure — reproducing "the caller's token is already cancelled by the
/// time the failure cleanup runs". Records the tokens handed to <see cref="DeleteGoalSessionAsync"/>
/// so the test can prove cleanup used an uncancelled token.
/// </summary>
file sealed class CancellingOnPlanBrain : IDistributedBrain
{
    private readonly CancellationTokenSource _cts;
    private readonly PlanResult _result;

    internal CancellingOnPlanBrain(CancellationTokenSource cts, PlanResult result)
    {
        _cts = cts;
        _result = result;
    }

    /// <summary>Goal IDs whose Brain session was deleted.</summary>
    internal List<string> DeletedSessions { get; } = [];

    /// <summary>Tokens passed to <see cref="DeleteGoalSessionAsync"/>, in call order.</summary>
    internal List<CancellationToken> DeleteSessionTokens { get; } = [];

    /// <summary>Signalled when cleanup (session deletion) has completed.</summary>
    internal TaskCompletionSource CleanupCompleted { get; } = new();

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<PlanResult> PlanIterationAsync(
        GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default)
    {
        // Cancel the caller's token, then report the planning failure. The failure cleanup that
        // follows must not use this now-cancelled token.
        _cts.Cancel();
        return Task.FromResult(_result);
    }

    public Task<PromptResult> CraftPromptAsync(
        GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
        Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));

    public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task EnsureBrainRepoAsync(
        string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<BrainResponse> AskQuestionAsync(
        string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
        Task.FromResult(BrainResponse.Answer("proceed"));

    public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default)
    {
        DeleteSessionTokens.Add(ct);
        DeletedSessions.Add(goalId);
        CleanupCompleted.TrySetResult();
        return Task.CompletedTask;
    }

    public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public bool GoalSessionExists(string goalId) => false;

    public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

    public BrainStats? GetStats() => null;
}

/// <summary>
/// In-memory <see cref="IGoalStore"/> that records status updates AND the token each update was
/// given, and that honours cancellation on the status write. This makes a cancelled cleanup token
/// observable: the write would throw and the goal would never reach Failed.
/// </summary>
internal sealed class CancellationAwareGoalStore : IGoalStore
{
    private readonly Goal _goal;

    internal CancellationAwareGoalStore(Goal goal) => _goal = goal;

    /// <summary>All status updates in the order they were applied.</summary>
    internal List<(GoalStatus Status, GoalUpdateMetadata? Metadata)> StatusUpdates { get; } = [];

    /// <summary>Tokens passed to <see cref="UpdateGoalStatusAsync"/>, in call order.</summary>
    internal List<CancellationToken> StatusUpdateTokens { get; } = [];

    public string Name => "cancellation-aware-store";

    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(_goal.Status == GoalStatus.Pending ? [_goal] : []);

    public Task UpdateGoalStatusAsync(
        string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
    {
        // Honour cancellation: a cancelled cleanup token must be visible as a failed write.
        ct.ThrowIfCancellationRequested();

        lock (StatusUpdates)
        {
            StatusUpdates.Add((status, metadata));
            StatusUpdateTokens.Add(ct);
        }

        _goal.Status = status;
        if (metadata?.FailureReason is not null)
            _goal.FailureReason = metadata.FailureReason;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([_goal]);

    public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult(goalId == _goal.Id ? _goal : null);

    public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default) => Task.FromResult(goal);

    public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) => Task.FromResult(false);

    public Task<IReadOnlyList<Goal>> SearchGoalsAsync(
        string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([]);

    public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([]);

    public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IterationSummary>>([]);

    public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) =>
        Task.FromResult(release);

    public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<Release?>(null);

    public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Release>>([]);

    public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) => Task.CompletedTask;

    public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) => Task.FromResult(false);

    public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([]);

    public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(
        string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConversationEntry>>([]);

    public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(
        int? limit = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(string, PersistedClarification)>>([]);
}

/// <summary>In-memory <see cref="IGoalStore"/> that records every status update applied to it.</summary>
internal sealed class PlanRejectRecordingGoalStore : IGoalStore
{
    private readonly Goal _goal;

    internal PlanRejectRecordingGoalStore(Goal goal) => _goal = goal;

    /// <summary>All status updates in the order they were applied.</summary>
    internal List<(GoalStatus Status, GoalUpdateMetadata? Metadata)> StatusUpdates { get; } = [];

    public string Name => "recording-store";

    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(_goal.Status == GoalStatus.Pending ? [_goal] : []);

    public Task UpdateGoalStatusAsync(
        string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
    {
        lock (StatusUpdates)
        {
            StatusUpdates.Add((status, metadata));
        }

        _goal.Status = status;
        if (metadata?.FailureReason is not null)
            _goal.FailureReason = metadata.FailureReason;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([_goal]);

    public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult(goalId == _goal.Id ? _goal : null);

    public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default) => Task.FromResult(goal);

    public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) => Task.FromResult(false);

    public Task<IReadOnlyList<Goal>> SearchGoalsAsync(
        string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([]);

    public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([]);

    public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IterationSummary>>([]);

    public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) =>
        Task.FromResult(release);

    public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<Release?>(null);

    public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Release>>([]);

    public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) => Task.CompletedTask;

    public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) => Task.FromResult(false);

    public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([]);

    public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(
        string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConversationEntry>>([]);

    public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(
        int? limit = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(string, PersistedClarification)>>([]);
}

/// <summary>
/// <see cref="IGoalStore"/> that throws on <see cref="UpdateGoalStatusAsync"/> to prove that
/// failure-safe cleanup steps still run when the primary status update fails.
/// </summary>
internal sealed class ThrowingUpdateGoalStore : IGoalStore
{
    private readonly Goal _goal;

    internal ThrowingUpdateGoalStore(Goal goal) => _goal = goal;

    public string Name => "throwing-update-store";

    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(_goal.Status == GoalStatus.Pending ? [_goal] : []);

    public Task UpdateGoalStatusAsync(
        string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
        throw new InvalidOperationException("goal store is offline");

    public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([_goal]);

    public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult(goalId == _goal.Id ? _goal : null);

    public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default) => Task.FromResult(goal);

    public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) => Task.FromResult(false);

    public Task<IReadOnlyList<Goal>> SearchGoalsAsync(
        string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([]);

    public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([]);

    public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IterationSummary>>([]);

    public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) =>
        Task.FromResult(release);

    public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<Release?>(null);

    public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Release>>([]);

    public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) => Task.CompletedTask;

    public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) => Task.FromResult(false);

    public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([]);

    public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(
        string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConversationEntry>>([]);

    public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(
        int? limit = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(string, PersistedClarification)>>([]);
}
