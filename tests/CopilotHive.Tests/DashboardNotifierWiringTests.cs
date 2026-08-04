using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using WorkerRole = CopilotHive.Workers.WorkerRole;
using BranchDeleteResult = CopilotHive.Git.BranchDeleteResult;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests for <see cref="DashboardNotifier"/> wiring into goal-related producers.
/// Covers the terminal-phase-transition scenario (scenario 6) where exactly 1 notification
/// must fire from <see cref="GoalLifecycleService.FinalizeGoalAsync"/>, NOT from the
/// dispatcher's non-terminal phase-transition check.
/// </summary>
public sealed class DashboardNotifierWiringTests
{
    // ── Scenario 6: Terminal phase transition → 1 notification (GoalLifecycleService only) ──

    /// <summary>
    /// When a pipeline reaches a terminal transition (Done) via the Merging phase,
    /// exactly 1 notification fires — from <see cref="GoalLifecycleService.FinalizeGoalAsync"/>.
    /// The dispatcher's non-terminal check (line: <c>if (pipeline.Phase is not Done/Failed)</c>)
    /// must NOT fire because the pipeline is Done after <c>MarkGoalCompletedAsync</c>.
    /// </summary>
    [Fact]
    public async Task HandleTaskCompletionAsync_TerminalTransitionCompleted_NotifiesOnceFromLifecycleOnly()
    {
        var brain = new FakeWiringBrain();
        var notifier = new DashboardNotifier();
        var notificationCount = 0;
        notifier.OnStateChanged += () => Interlocked.Increment(ref notificationCount);

        var (dispatcher, pipeline, taskId) = CreateDispatcherWithMergeSetup(
            GoalPhase.Review, brain, notifier);

        // Review phase completes with APPROVE verdict → Continue → Merging → PerformMergeAsync → Done
        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "Review approved.",
            Metrics = new TaskMetrics { Verdict = "APPROVE" },
        }, TestContext.Current.CancellationToken);

        // Pipeline should be Done (terminal transition)
        Assert.Equal(GoalPhase.Done, pipeline.Phase);

        // Exactly 1 notification — from GoalLifecycleService.FinalizeGoalAsync,
        // NOT from the dispatcher's non-terminal phase-transition check.
        Assert.Equal(1, notificationCount);
    }

    /// <summary>
    /// When brain is null, HandleTaskCompletionAsync calls MarkGoalCompletedAsync directly
    /// and returns. The notification fires from GoalLifecycleService only (1 notification),
    /// and the dispatcher's post-check is never reached.
    /// </summary>
    [Fact]
    public async Task HandleTaskCompletionAsync_NoBrain_TerminalCompletion_NotifiesOnceFromLifecycleOnly()
    {
        var notifier = new DashboardNotifier();
        var notificationCount = 0;
        notifier.OnStateChanged += () => Interlocked.Increment(ref notificationCount);

        var (dispatcher, pipeline, taskId) = CreateDispatcherNoBrain(notifier);

        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "Coding done.",
        }, TestContext.Current.CancellationToken);

        Assert.Equal(GoalPhase.Done, pipeline.Phase);
        Assert.Equal(1, notificationCount);
    }

    /// <summary>
    /// When a pipeline fails via the dispatcher (DriveNextPhaseAsync catches an exception),
    /// MarkGoalFailedAsync fires the notification from GoalLifecycleService (1 notification).
    /// The dispatcher's post-check sees Failed → does NOT fire.
    /// </summary>
    [Fact]
    public async Task HandleTaskCompletionAsync_TerminalTransitionFailed_NotifiesOnceFromLifecycleOnly()
    {
        // A brain that throws during PlanIterationAsync will cause DriveNextPhaseAsync to
        // throw when HandleNewIterationAsync tries to re-plan (Coding + FAIL → NewIteration).
        var brain = new FakeWiringBrain { ThrowOnPlan = true };
        var notifier = new DashboardNotifier();
        var notificationCount = 0;
        notifier.OnStateChanged += () => Interlocked.Increment(ref notificationCount);

        var (dispatcher, pipeline, taskId) = CreateDispatcherWithMergeSetup(
            GoalPhase.Coding, brain, notifier, maxRetries: 0);

        // Coding + FAIL → NewIteration → HandleNewIterationAsync → retry budget exhausted → MarkGoalFailedAsync.
        // Set GitStatus with FilesChanged > 0 to bypass the no-op detection path.
        await dispatcher.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = "Coding failed.",
            GitStatus = new GitChangeSummary { FilesChanged = 3, Pushed = true },
            Metrics = new TaskMetrics { Verdict = "FAIL" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(GoalPhase.Failed, pipeline.Phase);
        // Exactly 1 notification from GoalLifecycleService (terminal), not from dispatcher
        Assert.Equal(1, notificationCount);
    }

    // ── Scenario 9 (enhanced): Subscriber exception isolation with counter ──

    /// <summary>
    /// Two handlers: first throws, second increments a counter. Assert no exception
    /// propagates and the counter is exactly 1 (second handler ran despite first's exception).
    /// </summary>
    [Fact]
    public void DashboardNotifier_SubscriberException_DoesNotPropagate_SecondHandlerStillRuns()
    {
        var notifier = new DashboardNotifier();
        var counter = 0;

        notifier.OnStateChanged += () => throw new InvalidOperationException("boom");
        notifier.OnStateChanged += () => Interlocked.Increment(ref counter);

        var ex = Record.Exception(() => notifier.NotifyStateChanged());

        Assert.Null(ex);
        Assert.Equal(1, counter);
    }

    // ── Scenario 10 (enhanced): Concurrent unsubscription in a loop ──

    /// <summary>
    /// Genuinely races NotifyStateChanged against concurrent unsubscribe/resubscribe using a
    /// <see cref="Barrier"/>. Asserts no exception escapes and that a captured handler still ran
    /// while logically unsubscribed, proving the GetInvocationList() snapshot semantics.
    /// </summary>
    [Fact]
    public void DashboardNotifier_ConcurrentUnsubscribe_SnapshotSemanticsHold()
    {
        Assert.True(Dashboard.DashboardNotifierRaceHelper.RaceUnsubscribe());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static readonly List<GoalPhase> StandardPhases =
        [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging];

    private static (GoalDispatcher dispatcher, GoalPipeline pipeline, string taskId)
        CreateDispatcherWithMergeSetup(
            GoalPhase phase, IDistributedBrain brain, DashboardNotifier notifier, int maxRetries = 3)
    {
        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Test goal",
            RepositoryNames = ["test-repo"],
        };
        var goalSource = new WiringFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries);

        // Synchronize both pipeline.Phase and StateMachine.Phase to the target phase.
        pipeline.SetPlan(new IterationPlan { Phases = StandardPhases });
        pipeline.StateMachine.StartIteration(StandardPhases);
        // Advance the state machine to the target phase by transitioning through intermediate phases.
        // For Review: transition Coding(Succeeded) → Testing(Succeeded) → Review
        // For Merging: transition Coding(Succeeded) → Testing(Succeeded) → Review(Succeeded) → Merging
        // For Coding: already at Coding after StartIteration
        if (phase != GoalPhase.Coding)
        {
            pipeline.StateMachine.Transition(PhaseInput.Succeeded); // Coding → Testing
            if (phase != GoalPhase.Testing)
            {
                pipeline.StateMachine.Transition(PhaseInput.Succeeded); // Testing → Review
                if (phase != GoalPhase.Review)
                {
                    pipeline.StateMachine.Transition(PhaseInput.Succeeded); // Review → Merging
                }
            }
        }
        pipeline.AdvanceTo(phase);

        // Set CoderBranch so PerformMergeAsync doesn't fail with "No coder branch set"
        pipeline.CoderBranch = "feature/test-branch";

        var taskId = $"task-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);

        var config = new HiveConfigFile
        {
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "test-repo",
                    Url = "https://github.com/test/repo.git",
                    DefaultBranch = "main",
                },
            ],
        };

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new WiringFakeRepoManager(),
            brain,
            config: config,
            dashboardNotifier: notifier);

        return (dispatcher, pipeline, taskId);
    }

    private static (GoalDispatcher dispatcher, GoalPipeline pipeline, string taskId)
        CreateDispatcherNoBrain(DashboardNotifier notifier)
    {
        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Test goal",
        };
        var goalSource = new WiringFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);

        // Synchronize both pipeline.Phase and StateMachine.Phase to Coding.
        pipeline.SetPlan(new IterationPlan { Phases = StandardPhases });
        pipeline.StateMachine.StartIteration(StandardPhases);
        pipeline.AdvanceTo(GoalPhase.Coding);

        var taskId = $"task-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new WiringFakeRepoManager(),
            brain: null,
            dashboardNotifier: notifier);

        return (dispatcher, pipeline, taskId);
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    private class WiringFakeBrain : IDistributedBrain
    {
        public bool ThrowOnPlan { get; set; }

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) =>
            UpdateModelAsync(model, maxContextTokens, ct);

        public Task UpdateModelAsync(string model, int? maxContextTokens = null, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default)
        {
            if (ThrowOnPlan)
                throw new InvalidOperationException("Simulated Brain failure in PlanIterationAsync");
            return Task.FromResult(PlanResult.Success(IterationPlan.Default()));
        }

        public Task<PromptResult> CraftPromptAsync(
            GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));

        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult<string?>("Test merge commit");

        public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<BrainResponse> AskQuestionAsync(
            string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
            Task.FromResult(BrainResponse.Answer("Brain is not available."));

        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public bool GoalSessionExists(string goalId) => false;

        public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult("Goal completed.");

        public BrainStats? GetStats() => null;
    }

    /// <summary>Alias used by the terminal-transition test with merge setup.</summary>
    private sealed class FakeWiringBrain : WiringFakeBrain { }

    private sealed class WiringFakeRepoManager : IBrainRepoManager
    {
        public string WorkDirectory => "/fake/work";

        public Task<string> EnsureCloneAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
            Task.FromResult($"/fake/work/{repoName}");

        public Task<string> MergeFeatureBranchAsync(
            string repoName, string featureBranch, string defaultBranch, string commitMessage, CancellationToken ct = default) =>
            Task.FromResult("fake-merge-sha");

        public Task<BranchDeleteResult> DeleteRemoteBranchAsync(string repoName, string branchName, CancellationToken ct = default) =>
            Task.FromResult(BranchDeleteResult.Success);

        public string GetClonePath(string repoName) => $"/fake/work/{repoName}";

        public Task<string?> GetHeadShaAsync(string repoName, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<string?> MergeBranchAsync(string repoName, string sourceBranch, string targetBranch, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<bool> CreateTagAsync(string repoName, string tag, string branch, string message, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> DeleteTagAsync(string repoName, string tag, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<List<string>> ListRemoteBranchesAsync(string repoName, CancellationToken ct = default) =>
            Task.FromResult(new List<string>());
    }

    private sealed class WiringFakeGoalSource : IGoalSource
    {
        private readonly Goal _goal;

        public WiringFakeGoalSource(Goal goal) => _goal = goal;

        public string Name => "wiring-fake";

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([_goal]);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}