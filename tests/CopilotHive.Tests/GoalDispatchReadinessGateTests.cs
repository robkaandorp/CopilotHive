using System.Collections.Concurrent;
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

namespace CopilotHive.Tests;

/// <summary>
/// Tests for the all-or-nothing model-readiness dispatch gate in
/// <see cref="GoalDispatchService.DispatchNextGoalAsync"/>. The gate runs BEFORE
/// <c>GoalManager.GetNextGoalAsync</c> so a blocked goal stays Pending: no goal is
/// consumed, no pipeline/session/task is created. The check is synchronous, so all
/// tests are deterministic with no arbitrary delays.
/// </summary>
public sealed class GoalDispatchReadinessGateTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A collecting logger that captures every log entry (level + formatted message).
    /// </summary>
    private sealed class TestCollectingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Logs { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Logs.Add((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>
    /// Goal source that tracks whether <see cref="GetPendingGoalsAsync"/> was called.
    /// The readiness gate must NOT call <c>GetNextGoalAsync</c>, which means this source's
    /// <c>GetPendingGoalsAsync</c> must NOT be called when the gate blocks.
    /// </summary>
    private sealed class TrackingGoalSource : IGoalSource
    {
        private readonly Goal _goal;
        public int GetPendingCalls { get; private set; }

        public TrackingGoalSource(Goal goal) => _goal = goal;

        public string Name => "tracking";

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default)
        {
            GetPendingCalls++;
            return Task.FromResult<IReadOnlyList<Goal>>([_goal]);
        }

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Minimal brain stub for readiness gate tests. Most tests do NOT reach the brain
    /// (the gate blocks first); this stub is only used for the "fully configured" and
    /// "restart-after-fix" tests where dispatch proceeds past the gate.
    /// </summary>
    private class ReadyGateFakeBrain : IDistributedBrain
    {
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateModelAsync(string model, int? maxContextTokens,
            Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline,
            string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PlanResult.Success(IterationPlan.Default()));

        public Task<PromptResult> CraftPromptAsync(
            GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null,
            CancellationToken ct = default) =>
            Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));

        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline,
            CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task EnsureBrainRepoAsync(string repoName, string repoUrl,
            string defaultBranch, CancellationToken ct = default) => Task.CompletedTask;

        public Task InjectOrchestratorInstructionsAsync(string instructions,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note,
            CancellationToken ct) => Task.CompletedTask;

        public Task<BrainResponse> AskQuestionAsync(
            string goalId, int iteration, string phase, string workerRole,
            string question, CancellationToken ct = default) =>
            Task.FromResult(BrainResponse.Answer("proceed"));

        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public virtual Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RegisterExistingGoalSessionAsync(string goalId,
            CancellationToken ct = default) => Task.CompletedTask;

        public bool GoalSessionExists(string goalId) => false;

        public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline,
            CancellationToken ct = default) =>
            Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

        public BrainStats? GetStats() => null;
    }

    /// <summary>
    /// A brain stub that records <see cref="ForkSessionForGoalAsync"/> calls so we can
    /// verify no session was created when the gate blocks.
    /// </summary>
    private sealed class ForkTrackingGateBrain : ReadyGateFakeBrain
    {
        public List<string> ForkCalls { get; } = [];

        public override Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default)
        {
            ForkCalls.Add(goalId);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Builds a <see cref="GoalDispatchService"/> with a tracking goal source and a
    /// collecting logger. The brain and config are parameterised so each test can
    /// configure (or omit) models to exercise the readiness gate.
    /// </summary>
    private static (GoalDispatchService service, TrackingGoalSource goalSource,
        GoalPipelineManager pipelineManager, TaskQueue taskQueue, TestCollectingLogger logger)
        CreateDispatchService(
            Goal goal,
            IDistributedBrain? brain,
            HiveConfigFile? config)
    {
        var goalSource = new TrackingGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);

        var pipelineManager = new GoalPipelineManager();
        var taskQueue = new TaskQueue();
        var workerGateway = new GrpcWorkerGateway(new WorkerPool());

        var logger = new TestCollectingLogger();

        var clarificationHandler = new ClarificationHandler(brain, null, null, NullLogger.Instance);
        var lifecycleService = new GoalLifecycleService(
            goalManager, NullLogger.Instance, eventBus: null);
        var maintenance = new DispatcherMaintenance(
            pipelineManager, goalManager, taskQueue, workerGateway,
            brain: null, agentsManager: null, configRepo: null,
            new ConcurrentQueue<string>(), NullLogger.Instance, config: config);
        var taskBuilder = new TaskBuilder(new BranchCoordinator());

        var taskDispatchService = new TaskDispatchService(
            taskQueue, workerGateway, taskBuilder, config,
            NullLogger<TaskDispatchService>.Instance, pipelineManager, lifecycleService, maintenance);

        var service = new GoalDispatchService(
            goalManager, pipelineManager, brain, config,
            taskDispatchService, clarificationHandler, null,
            null, null, null, logger, eventBus: null);

        return (service, goalSource, pipelineManager, taskQueue, logger);
    }

    private static Goal CreatePendingGoal() =>
        new()
        {
            Id = $"goal-ready-{Guid.NewGuid():N}",
            Description = "Readiness gate test goal",
            Status = GoalStatus.Pending,
            RepositoryNames = ["test-repo"],
        };

    /// <summary>
    /// Config with the repository AND all broadcastable role models AND the Brain model.
    /// This is the "fully configured" baseline that passes the readiness gate.
    /// </summary>
    private static HiveConfigFile FullConfig() =>
        new()
        {
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "test-repo",
                    Url = "https://github.com/test/repo",
                    DefaultBranch = "main",
                },
            ],
            Orchestrator = new OrchestratorConfig { Model = "brain-model" },
            Workers = TestHelpers.AllBroadcastableRoleModels(),
        };

    // ── Tests ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Test 1: Fully configured (Brain + all broadcastable roles) ⇒ dispatch proceeds
    /// as today. The goal is consumed (GetNextGoalAsync called), a pipeline is created,
    /// and a task is dispatched.
    /// </summary>
    [Fact]
    public async Task DispatchNextGoal_FullyConfigured_DispatchesGoal()
    {
        var ct = TestContext.Current.CancellationToken;
        var goal = CreatePendingGoal();
        var brain = new ForkTrackingGateBrain();
        var config = FullConfig();

        WorkTask? dispatchedTask = null;
        var (service, goalSource, pipelineManager, taskQueue, logger) =
            CreateDispatchService(goal, brain, config);
        taskQueue.OnEnqueue = t => dispatchedTask = t;

        await service.DispatchNextGoalAsync(ct);

        // The goal was consumed — GetPendingGoalsAsync was called by GetNextGoalAsync.
        Assert.True(goalSource.GetPendingCalls > 0,
            "GetNextGoalAsync should have been called when fully configured.");

        // A pipeline was created for the goal.
        var pipeline = pipelineManager.GetByGoalId(goal.Id);
        Assert.NotNull(pipeline);

        // A Brain session was forked.
        Assert.Contains(goal.Id, brain.ForkCalls);

        // A task was dispatched to a worker.
        Assert.NotNull(dispatchedTask);
    }

    /// <summary>
    /// Test 2: Any single broadcastable role unconfigured (e.g. reviewer) ⇒ blocked:
    /// the goal stays Pending, GetNextGoalAsync NOT called, no pipeline/session/task,
    /// and a clear log line names the missing role.
    /// </summary>
    [Fact]
    public async Task DispatchNextGoal_ReviewerUnconfigured_BlocksAndLogsMissingRole()
    {
        var ct = TestContext.Current.CancellationToken;
        var goal = CreatePendingGoal();
        var brain = new ForkTrackingGateBrain();

        // Config with all roles EXCEPT reviewer — reviewer is unconfigured.
        var config = FullConfig();
        config.Workers.Remove("reviewer");

        var (service, goalSource, pipelineManager, taskQueue, logger) =
            CreateDispatchService(goal, brain, config);

        await service.DispatchNextGoalAsync(ct);

        // GetNextGoalAsync was NOT called — the goal source was never queried.
        Assert.Equal(0, goalSource.GetPendingCalls);

        // No pipeline was created.
        Assert.Null(pipelineManager.GetByGoalId(goal.Id));

        // No Brain session was forked.
        Assert.Empty(brain.ForkCalls);

        // No task was dispatched.
        // (TaskQueue has no direct "count" but no pipeline → no task possible.
        //  The pipeline-null assertion above covers this.)

        // A clear log line names the missing role.
        var blockedLog = logger.Logs.FirstOrDefault(l =>
            l.Level == LogLevel.Warning &&
            l.Message.Contains("goal not dispatched", StringComparison.OrdinalIgnoreCase) &&
            l.Message.Contains("reviewer", StringComparison.OrdinalIgnoreCase));
        Assert.True(blockedLog != default,
            $"Expected a 'goal not dispatched' warning naming 'reviewer'. " +
            $"Logs: {string.Join(", ", logger.Logs.Select(l => $"[{l.Level}] {l.Message}"))}");
    }

    /// <summary>
    /// Test 3: No Brain (no Orchestrator.Model / brain is null) ⇒ blocked (Brain gate).
    /// The goal stays Pending, GetNextGoalAsync NOT called, and the log names the Brain.
    /// </summary>
    [Fact]
    public async Task DispatchNextGoal_NoBrain_BlocksAndLogsBrainNotConfigured()
    {
        var ct = TestContext.Current.CancellationToken;
        var goal = CreatePendingGoal();

        // Config with all role models but NO Orchestrator.Model (Brain is null).
        var config = FullConfig();
        config.Orchestrator = new OrchestratorConfig();

        var (service, goalSource, pipelineManager, taskQueue, logger) =
            CreateDispatchService(goal, brain: null, config);

        await service.DispatchNextGoalAsync(ct);

        // GetNextGoalAsync was NOT called.
        Assert.Equal(0, goalSource.GetPendingCalls);

        // No pipeline was created.
        Assert.Null(pipelineManager.GetByGoalId(goal.Id));

        // A clear log line names the Brain as unconfigured.
        var blockedLog = logger.Logs.FirstOrDefault(l =>
            l.Level == LogLevel.Warning &&
            l.Message.Contains("goal not dispatched", StringComparison.OrdinalIgnoreCase) &&
            l.Message.Contains("brain", StringComparison.OrdinalIgnoreCase));
        Assert.True(blockedLog != default,
            $"Expected a 'goal not dispatched' warning mentioning 'brain'. " +
            $"Logs: {string.Join(", ", logger.Logs.Select(l => $"[{l.Level}] {l.Message}"))}");
    }

    /// <summary>
    /// Test 4: Blank/whitespace model ⇒ blocked (unconfigured). A whitespace-only model
    /// normalizes to null via <c>HiveConfigFile.GetModelForRole(string)</c>, so the
    /// readiness gate must treat it as unconfigured.
    /// </summary>
    [Theory]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("")]
    public async Task DispatchNextGoal_BlankOrWhitespaceModel_BlocksAsUnconfigured(string blankModel)
    {
        var ct = TestContext.Current.CancellationToken;
        var goal = CreatePendingGoal();
        var brain = new ForkTrackingGateBrain();

        // Config with all roles configured EXCEPT coder, whose model is blank/whitespace.
        var config = FullConfig();
        config.Workers["coder"] = new WorkerConfig { Model = blankModel };

        var (service, goalSource, pipelineManager, taskQueue, logger) =
            CreateDispatchService(goal, brain, config);

        await service.DispatchNextGoalAsync(ct);

        // GetNextGoalAsync was NOT called.
        Assert.Equal(0, goalSource.GetPendingCalls);

        // No pipeline was created.
        Assert.Null(pipelineManager.GetByGoalId(goal.Id));

        // No Brain session was forked.
        Assert.Empty(brain.ForkCalls);

        // A clear log line names the coder role as unconfigured.
        var blockedLog = logger.Logs.FirstOrDefault(l =>
            l.Level == LogLevel.Warning &&
            l.Message.Contains("goal not dispatched", StringComparison.OrdinalIgnoreCase) &&
            l.Message.Contains("coder", StringComparison.OrdinalIgnoreCase));
        Assert.True(blockedLog != default,
            $"Expected a 'goal not dispatched' warning naming 'coder' for blank model '{blankModel}'. " +
            $"Logs: {string.Join(", ", logger.Logs.Select(l => $"[{l.Level}] {l.Message}"))}");
    }

    /// <summary>
    /// Test 5: MergeWorker unconfigured alone ⇒ dispatch proceeds (not broadcastable).
    /// MergeWorker is NOT in WorkerRoles.BroadcastableRoles, so an unconfigured MergeWorker
    /// model must not block dispatch.
    /// </summary>
    [Fact]
    public async Task DispatchNextGoal_MergeWorkerUnconfigured_DispatchesGoal()
    {
        var ct = TestContext.Current.CancellationToken;
        var goal = CreatePendingGoal();
        var brain = new ForkTrackingGateBrain();

        // Full config with all broadcastable roles. MergeWorker is intentionally absent
        // (it is not in BroadcastableRoles, so its absence must not block dispatch).
        var config = FullConfig();
        // Verify MergeWorker is NOT in the config's Workers (it's not added by AllBroadcastableRoleModels).
        Assert.False(config.Workers.ContainsKey("mergeworker"));

        WorkTask? dispatchedTask = null;
        var (service, goalSource, pipelineManager, taskQueue, logger) =
            CreateDispatchService(goal, brain, config);
        taskQueue.OnEnqueue = t => dispatchedTask = t;

        await service.DispatchNextGoalAsync(ct);

        // The goal was consumed.
        Assert.True(goalSource.GetPendingCalls > 0,
            "GetNextGoalAsync should have been called — MergeWorker is not broadcastable.");

        // A pipeline was created.
        Assert.NotNull(pipelineManager.GetByGoalId(goal.Id));

        // A task was dispatched.
        Assert.NotNull(dispatchedTask);
    }

    /// <summary>
    /// Test 6: A blocked goal dispatches after the config completes (restart-after-fix
    /// acceptable). First dispatch with a missing role is blocked; then the config is
    /// completed and a second dispatch on the SAME service instance proceeds.
    /// </summary>
    [Fact]
    public async Task DispatchNextGoal_BlockedThenConfigCompleted_DispatchesAfterFix()
    {
        var ct = TestContext.Current.CancellationToken;
        var goal = CreatePendingGoal();
        var brain = new ForkTrackingGateBrain();

        // Start with a config missing the improver model.
        var config = FullConfig();
        config.Workers.Remove("improver");

        WorkTask? dispatchedTask = null;
        var (service, goalSource, pipelineManager, taskQueue, logger) =
            CreateDispatchService(goal, brain, config);
        taskQueue.OnEnqueue = t => dispatchedTask = t;

        // Act 1: first dispatch — blocked because improver is unconfigured.
        await service.DispatchNextGoalAsync(ct);

        Assert.Equal(0, goalSource.GetPendingCalls);
        Assert.Null(pipelineManager.GetByGoalId(goal.Id));
        Assert.Empty(brain.ForkCalls);
        Assert.Null(dispatchedTask);

        var blockedLog1 = logger.Logs.FirstOrDefault(l =>
            l.Level == LogLevel.Warning &&
            l.Message.Contains("improver", StringComparison.OrdinalIgnoreCase));
        Assert.True(blockedLog1 != default,
            "First dispatch should log a blocked warning naming 'improver'.");

        // Act 2: complete the config (add the improver model), then dispatch again.
        // This simulates a restart-after-fix: the config singleton is mutated in place.
        config.Workers["improver"] = new WorkerConfig { Model = "test-improver-model" };

        await service.DispatchNextGoalAsync(ct);

        // The goal was consumed on the second dispatch.
        Assert.True(goalSource.GetPendingCalls > 0,
            "GetNextGoalAsync should have been called after the config was completed.");

        // A pipeline was created.
        Assert.NotNull(pipelineManager.GetByGoalId(goal.Id));

        // A Brain session was forked.
        Assert.Contains(goal.Id, brain.ForkCalls);

        // A task was dispatched.
        Assert.NotNull(dispatchedTask);
    }
}