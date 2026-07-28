using System.Collections.Concurrent;
using CopilotHive.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Improvement;
using CopilotHive.Knowledge;
using CopilotHive.Metrics;
using CopilotHive.Orchestration;
using CopilotHive.Workers;
using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Services;

/// <summary>
/// Background service that converts pending goals into multi-phase pipeline tasks
/// using the Brain for intelligent prompt crafting and decision-making.
/// Handles both new goal dispatch and task completion callbacks.
/// </summary>
public sealed class GoalDispatcher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AgentsSyncInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BranchCleanupInterval = TimeSpan.FromHours(1);

    private readonly GoalManager _goalManager;
    private readonly GoalPipelineManager _pipelineManager;
    private readonly TaskQueue _taskQueue;
    private readonly IWorkerGateway _workerGateway;
    private readonly IDistributedBrain? _brain;
    private readonly IBrainRepoManager _repoManager;
    private readonly ILogger<GoalDispatcher> _logger;
    private readonly HiveConfigFile? _config;
    private readonly KnowledgeGraph? _knowledgeGraph;
    private readonly ConfigRepoManager? _configRepo;
    private readonly IGoalStore? _goalStore;

    private readonly BranchCoordinator _branchCoordinator = new();
    private readonly TaskBuilder _taskBuilder = new(new BranchCoordinator());
    private readonly ConcurrentQueue<string> _redispatchQueue = new();
    private readonly SemaphoreSlim _resumeLock = new(1, 1);
    private readonly TimeSpan _startupDelay;
    private readonly IClarificationRouter? _clarificationRouter;
    private readonly ClarificationQueueService? _clarificationQueue;
    private readonly ProgressLog? _progressLog;
    private readonly ClarificationHandler _clarificationHandler;
    private readonly GoalLifecycleService _lifecycleService;
    private readonly PipelineDriver _pipelineDriver;
    private readonly DispatcherMaintenance _maintenance;
    private readonly TaskDispatchService _taskDispatchService;
    private readonly TaskCompletionService _taskCompletionService;
    private readonly GoalDispatchService _goalDispatchService;
    private readonly DashboardNotifier? _dashboardNotifier;
    private DateTime _lastBranchCleanup = DateTime.MinValue;

    /// <summary>
    /// Initialises a new <see cref="GoalDispatcher"/> with required and optional dependencies.
    /// </summary>
    /// <param name="goalManager">Source of pending goals.</param>
    /// <param name="pipelineManager">Tracks active goal pipelines.</param>
    /// <param name="taskQueue">Queue used to dispatch task assignments to workers.</param>
    /// <param name="workerGateway">Abstraction for communicating with connected workers.</param>
    /// <param name="completionNotifier">Bridge that delivers task completion events to this dispatcher.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="brain">Optional LLM brain for intelligent prompt crafting.</param>
    /// <param name="config">Optional hive configuration from the config repo.</param>
    /// <param name="metricsTracker">Optional metrics tracker for the improvement cycle.</param>
    /// <param name="agentsManager">Optional manager for per-role AGENTS.md files.</param>
    /// <param name="improvementAnalyzer">Optional analyzer that decides when to run the improver.</param>
    /// <param name="configRepo">Optional config repo manager for syncing AGENTS.md files.</param>
    /// <param name="repoManager">Brain repo manager for persistent repo clones and merge operations.</param>
    /// <param name="clarificationRouter">Optional clarification router for Composer auto-answer.</param>
    /// <param name="clarificationQueue">Optional clarification queue for human escalation.</param>
    /// <param name="startupDelay">Delay before the first dispatch poll; defaults to 10 seconds to give workers time to connect.</param>
    /// <param name="progressLog">Optional progress log for recording clarification events.</param>
    /// <param name="knowledgeGraph">Optional knowledge graph for reloading on sync cycles.</param>
    /// <param name="goalStore">Optional goal store for direct CRUD operations such as branch cleanup.</param>
    /// <param name="dashboardNotifier">Optional dashboard notifier for state-change events.</param>
    public GoalDispatcher(
        GoalManager goalManager,
        GoalPipelineManager pipelineManager,
        TaskQueue taskQueue,
        IWorkerGateway workerGateway,
        TaskCompletionNotifier completionNotifier,
        ILogger<GoalDispatcher> logger,
        IBrainRepoManager repoManager,
        IDistributedBrain? brain = null,
        HiveConfigFile? config = null,
        MetricsTracker? metricsTracker = null,
        AgentsManager? agentsManager = null,
        ImprovementAnalyzer? improvementAnalyzer = null,
        ConfigRepoManager? configRepo = null,
        IClarificationRouter? clarificationRouter = null,
        ClarificationQueueService? clarificationQueue = null,
        TimeSpan? startupDelay = null,
        ProgressLog? progressLog = null,
        KnowledgeGraph? knowledgeGraph = null,
        IGoalStore? goalStore = null,
        DashboardNotifier? dashboardNotifier = null)
    {
        _repoManager = repoManager ?? throw new ArgumentNullException(nameof(repoManager));
        _goalManager = goalManager;
        _pipelineManager = pipelineManager;
        _taskQueue = taskQueue;
        _workerGateway = workerGateway;
        _brain = brain;
        _logger = logger;
        _config = config;
        _clarificationRouter = clarificationRouter;
        _clarificationQueue = clarificationQueue;
        _startupDelay = startupDelay ?? TimeSpan.FromSeconds(10);
        _progressLog = progressLog;
        _knowledgeGraph = knowledgeGraph;
        _configRepo = configRepo;
        _goalStore = goalStore;
        _dashboardNotifier = dashboardNotifier;
        _clarificationHandler = new ClarificationHandler(brain, clarificationRouter, clarificationQueue, logger);

        _lifecycleService = new GoalLifecycleService(
            goalManager, logger, metricsTracker, agentsManager, configRepo, brain, dashboardNotifier);

        _maintenance = new DispatcherMaintenance(
            pipelineManager, goalManager, taskQueue, workerGateway, brain,
            agentsManager, configRepo, _redispatchQueue, logger,
            knowledgeGraph,
            goalStore: goalStore,
            repoManager: repoManager,
            config: config);

        _taskDispatchService = new TaskDispatchService(
            _taskQueue, _workerGateway, _taskBuilder, _config,
            NullLogger<TaskDispatchService>.Instance, _pipelineManager, _lifecycleService, _maintenance);

        _pipelineDriver = new PipelineDriver(
            brain: brain,
            lifecycleService: _lifecycleService,
            goalManager: goalManager,
            repoManager: repoManager,
            improvementAnalyzer: improvementAnalyzer,
            agentsManager: agentsManager,
            metricsTracker: metricsTracker,
            dispatchToRole: DispatchToRole,
            resolvePrompt: ResolvePromptAsync,
            resolvePlan: ResolvePlanAsync,
            resolveRepositories: ResolveRepositories,
            syncAgents: ct => _maintenance.SyncAgentsFromConfigRepoAsync(ct),
            generateMergeCommitMessage: GenerateMergeCommitMessageAsync,
            logger: logger,
            knowledgeGraph: _knowledgeGraph,
            configRepo: _configRepo);

        _taskCompletionService = new TaskCompletionService(
            _pipelineManager, _brain, _pipelineDriver, _lifecycleService,
            _dashboardNotifier, _logger);

        _goalDispatchService = new GoalDispatchService(
            _goalManager, _pipelineManager, _brain, _config,
            _taskDispatchService, _clarificationHandler, _knowledgeGraph,
            _goalStore, _configRepo, _dashboardNotifier, _logger);

        completionNotifier.OnTaskCompleted+= result => HandleTaskCompletionAsync(result);
    }

    /// <summary>
    /// Cancels an InProgress or Pending goal. If a pipeline exists, it is moved to the Failed
    /// state. The goal status is set to Failed with reason "Cancelled by user".
    /// Returns true if the goal was cancelled, false if it was not in a cancellable state.
    ///
    /// Note: The current worker task may still be running when cancel is called.
    /// That's OK — when the worker reports back via HandleTaskCompletionAsync,
    /// the pipeline will already be in Failed state, so the result will be ignored
    /// (the existing early-exit check at the top of HandleTaskCompletionAsync handles this).
    /// </summary>
    /// <param name="goalId">The goal to cancel.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <c>true</c> if the goal was successfully cancelled; <c>false</c> if the goal
    /// is already Done, Completed, or Failed and cannot be cancelled.
    /// </returns>
    public async Task<bool> CancelGoalAsync(string goalId, CancellationToken ct = default)
    {
        var pipeline = _pipelineManager.GetByGoalId(goalId);

        if (pipeline is not null)
        {
            // InProgress goal — active pipeline exists
            if (pipeline.Phase is GoalPhase.Done or GoalPhase.Failed)
                return false;

            await _lifecycleService.MarkGoalFailedAsync(pipeline, "Cancelled by user", ct);
            _pipelineManager.RemovePipeline(goalId);
            _logger.LogInformation("Goal {GoalId} cancelled (was InProgress, phase={Phase})", goalId, pipeline.Phase);
            if (_brain is not null)
                await _brain.DeleteGoalSessionAsync(goalId);
            return true;
        }

        // No active pipeline — check the store for Pending goals
        var goal = await _goalManager.GetGoalAsync(goalId, ct);
        if (goal is null)
            return false;

        if (goal.Status is not (GoalStatus.InProgress or GoalStatus.Pending))
            return false;

        await _goalManager.UpdateGoalStatusAsync(goalId, GoalStatus.Failed,
            new GoalUpdateMetadata { FailureReason = "Cancelled by user" }, ct);
        _dashboardNotifier?.NotifyStateChanged();
        _logger.LogInformation("Goal {GoalId} cancelled (was {Status})", goalId, goal.Status);
        if (_brain is not null)
            await _brain.DeleteGoalSessionAsync(goalId);
        return true;
    }

    /// <summary>
    /// Enqueue a goal for re-dispatch. Called by <see cref="StaleWorkerCleanupService"/>
    /// when a dead worker's task is cleared, or by <see cref="RestoreActivePipelinesAsync"/>
    /// when a stale mid-task pipeline is found on startup.
    /// </summary>
    public void EnqueueRedispatch(string goalId)
    {
        _redispatchQueue.Enqueue(goalId);
    }

    /// <summary>
    /// Determines whether a goal failed specifically due to iteration-budget exhaustion,
    /// making it eligible for resumption via <see cref="ResumeGoalAsync"/>.
    /// Matches the failure reasons produced by <see cref="PipelineDriver"/>:
    /// "Exceeded max iterations" and "Exceeded max iterations during merge conflict resolution".
    /// </summary>
    private static bool IsIterationExhaustionFailure(Goal goal)
    {
        if (goal.Status != GoalStatus.Failed)
            return false;
        if (string.IsNullOrEmpty(goal.FailureReason))
            return false;
        var reason = goal.FailureReason;
        return reason.Contains("Exceeded max iterations", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("max iterations", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resumes a goal that failed due to iteration exhaustion by extending its iteration budget
    /// and dispatching a new iteration. Serialized via a global lock.
    /// </summary>
    /// <param name="goalId">The goal to resume.</param>
    /// <param name="additionalIterations">Number of additional iterations to grant (1-1000).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the goal was resumed; false if the goal is not eligible or the goal store is unavailable.</returns>
    public async Task<bool> ResumeGoalAsync(string goalId, int additionalIterations, CancellationToken ct = default)
    {
        // Resolve goal store — mandatory for resume
        var goalStore = _goalStore;
        if (goalStore is null)
            return false;

        // Check goal eligibility BEFORE acquiring lock
        var goal = await goalStore.GetGoalAsync(goalId, ct);
        if (goal is null || !IsIterationExhaustionFailure(goal))
            return false;

        var lockObj = _resumeLock;
        await lockObj.WaitAsync(ct);
        try
        {
            // Re-check FULL eligibility inside lock (could have changed)
            goal = await goalStore.GetGoalAsync(goalId, ct);
            if (goal is null || !IsIterationExhaustionFailure(goal))
                return false;

            // Load pipeline
            var pipeline = _pipelineManager.GetByGoalId(goalId);
            if (pipeline is null)
            {
                pipeline = _pipelineManager.RestorePipeline(goalId);
                if (pipeline is null)
                    return false;
            }

            // Require pipeline in Failed phase
            if (pipeline.Phase != GoalPhase.Failed)
                return false;

            // Extend budget
            pipeline.ExtendIterations(additionalIterations);

            // Consume one iteration for the resumed iteration
            if (!pipeline.IterationBudget.TryConsume())
                return false; // shouldn't happen after TopUp

            // Capture stale task ID before clearing
            var staleTaskId = pipeline.ActiveTaskId;
            if (staleTaskId is not null)
            {
                _pipelineManager.UnregisterTask(staleTaskId);
                pipeline.ClearActiveTask();
            }

            // Clear terminal state
            pipeline.ClearCompletedAt();
            pipeline.StateMachine.ResetToPlanning();
            pipeline.AdvanceTo(GoalPhase.Planning);
            pipeline.SetPlan(IterationPlan.Default());
            pipeline.Metrics.ResetForNewIteration(pipeline.Iteration);

            // Update goal status FIRST (DB is source of truth)
            // If this throws, goal stays Failed — no pipeline mutation has been persisted
            goal.Status = GoalStatus.InProgress;
            goal.FailureReason = null;
            goal.CompletedAt = null;
            await goalStore.UpdateGoalAsync(goal, ct);

            // Persist pipeline at recovery boundary
            _pipelineManager.PersistFull(pipeline);

            // Re-register with Brain and fork session (best-effort)
            try
            {
                (_brain as DistributedBrain)?.RegisterActivePipeline(pipeline);
                if (_brain is not null)
                    await _brain.ForkSessionForGoalAsync(goalId, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to re-register/fork Brain session for resumed goal '{GoalId}'", goalId);
            }

            // Plan a new iteration (best-effort)
            IterationPlan validatedPlan;
            try
            {
                var rawPlan = await ResolvePlanAsync(pipeline, null, ct);
                validatedPlan = IterationPlanValidator.ValidatePlan(rawPlan);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Brain failed to plan resumed iteration for {GoalId} — using default plan", goalId);
                validatedPlan = IterationPlan.Default();
            }

            pipeline.SetPlan(validatedPlan);
            pipeline.StateMachine.StartIteration(validatedPlan.Phases);
            var firstPhase = validatedPlan.Phases[0];
            pipeline.AdvanceTo(firstPhase);
            pipeline.PhaseLog.Add(PhaseResult.Create(firstPhase, pipeline.Iteration, 1));

            // Craft prompt (best-effort)
            string prompt;
            try
            {
                prompt = _brain is not null
                    ? await ResolvePromptAsync(pipeline, firstPhase, null, ct)
                    : BuildCoderPrompt(pipeline.Goal);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Brain failed to craft prompt for resumed {GoalId} — using fallback", goalId);
                prompt = BuildCoderPrompt(pipeline.Goal);
            }

            // Dispatch (best-effort — if this fails, goal is InProgress and dispatch loop handles it)
            try
            {
                await DispatchToRole(pipeline, firstPhase.ToWorkerRole(), prompt, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dispatch resumed goal '{GoalId}' — enqueuing for redispatch", goalId);
                // CRITICAL: Clear ActiveTaskId before enqueuing — DrainRedispatchQueueAsync
                // skips pipelines with non-null ActiveTaskId.
                pipeline.ClearActiveTask();
                _redispatchQueue.Enqueue(goalId);
            }

            _pipelineManager.PersistFull(pipeline);
            return true;
        }
        finally
        {
            lockObj.Release();
        }
    }

    /// <summary>
    /// Clears all dispatcher runtime state for a goal that is being retried (Failed→Draft).
    /// Removes the stale pipeline from the <see cref="GoalPipelineManager"/> so the
    /// dispatcher does not see an active pipeline blocking new goal dispatch.
    /// </summary>
    /// <param name="goalId">The goal being retried.</param>
    public void ClearGoalRetryState(string goalId)
    {
        if (_brain is not null)
        {
            _ = Task.Run(async () =>
            {
                try { await _brain.DeleteGoalSessionAsync(goalId); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete goal session for {GoalId}", goalId); }
            });
        }
        _pipelineManager.RemovePipeline(goalId);
        _logger.LogInformation("Cleared dispatcher retry state for goal {GoalId}", goalId);
    }

    /// <summary>
    /// Handles a question from a worker tool call by routing it to the Brain via
    /// <see cref="IDistributedBrain.AskQuestionAsync"/>. If the Brain returns an escalation
    /// response, the question is forwarded to the Composer LLM for auto-answer. If the
    /// Composer cannot answer, the question is queued for human resolution with a 5-minute
    /// timeout. Returns the resolved answer as a string.
    /// </summary>
    public Task<string> AskBrainAsync(GoalPipeline pipeline, string question, CancellationToken ct)
        => _clarificationHandler.AskBrainAsync(pipeline, question, ct);

    /// <summary>
    /// Records a clarification Q&amp;A into the pipeline and emits a structured log entry.
    /// Delegated to <see cref="ClarificationHandler"/>.
    /// </summary>
    private void RecordClarification(GoalPipeline pipeline, string question, string answer, string answeredBy)
        => _clarificationHandler.RecordClarification(pipeline, question, answer, answeredBy);

    /// <summary>
    /// Routes a Brain escalation through the clarification pipeline and returns
    /// the resolved answer (from Composer, human, or timeout fallback).
    /// Delegated to <see cref="ClarificationHandler"/>.
    /// </summary>
    private Task<string> RouteEscalationAsync(
        GoalPipeline pipeline, string question, string reason, CancellationToken ct)
        => _clarificationHandler.RouteEscalationAsync(pipeline, question, reason, ct);

    /// <summary>
    /// Calls <see cref="IDistributedBrain.PlanIterationAsync"/> and handles any escalation
    /// by routing to the clarification pipeline. On successful clarification, retries planning
    /// with the answer as additional context. On timeout, returns the default plan.
    /// Exposed as <c>internal</c> for unit testing via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal Task<IterationPlan> ResolvePlanAsync(
        GoalPipeline pipeline, string? additionalContext, CancellationToken ct)
        => _clarificationHandler.ResolvePlanAsync(pipeline, additionalContext, ct);

    /// <summary>
    /// Calls <see cref="IDistributedBrain.CraftPromptAsync"/> and handles any escalation
    /// by routing to the clarification pipeline. On successful clarification, retries prompt
    /// crafting with the answer as additional context. On timeout, returns a fallback prompt.
    /// Exposed as <c>internal</c> for unit testing via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal Task<string> ResolvePromptAsync(
        GoalPipeline pipeline, GoalPhase phase, string? additionalContext, CancellationToken ct)
        => _clarificationHandler.ResolvePromptAsync(pipeline, phase, additionalContext, ct);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("GoalDispatcher starting with {SourceCount} goal source(s)", _goalManager.Sources.Count);

        _logger.LogInformation("GoalDispatcher started — polling for goals every {Interval}s (Brain: {BrainEnabled})",
            PollInterval.TotalSeconds, _brain is not null ? "enabled" : "disabled");

        // Restore any in-flight pipelines from the persistence store
        await RestoreActivePipelinesAsync(stoppingToken);

        // Sync agents from config repo at startup
        await SyncAgentsFromConfigRepoAsync(stoppingToken);

        // Give workers time to connect before dispatching
        await Task.Delay(_startupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow - _maintenance.LastAgentsSync > AgentsSyncInterval)
                {
                    await SyncAgentsFromConfigRepoAsync(stoppingToken);
                }

                // Periodic branch cleanup for completed goals
                if (DateTime.UtcNow - _lastBranchCleanup > BranchCleanupInterval)
                {
                    await _maintenance.CleanupMergedBranchesAsync(stoppingToken);
                    _lastBranchCleanup = DateTime.UtcNow;
                }

                await DrainRedispatchQueueAsync(stoppingToken);
                await DispatchNextGoalAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GoalDispatcher error");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }

        _logger.LogInformation("GoalDispatcher stopped");
    }

    /// <summary>
    /// Called by HiveOrchestratorService when a worker completes a task.
    /// Drives the pipeline to its next phase using the Brain.
    /// </summary>
    public Task HandleTaskCompletionAsync(TaskResult result, CancellationToken ct = default)
        => _taskCompletionService.HandleTaskCompletionAsync(result, ct);

    // ── Forwarding wrappers for instance methods (tests call via reflection) ──

    private Task MarkGoalCompleted(GoalPipeline pipeline, CancellationToken ct)
        => _lifecycleService.MarkGoalCompletedAsync(pipeline, ct);

    private Task MarkGoalFailed(GoalPipeline pipeline, string reason, CancellationToken ct)
        => _lifecycleService.MarkGoalFailedAsync(pipeline, reason, ct);

    private Task HandleNewIterationAsync(GoalPipeline pipeline, string verdict, CancellationToken ct)
        => _pipelineDriver.HandleNewIterationAsync(pipeline, verdict, ct);

    private Task HandleMergeFailureAsync(GoalPipeline pipeline, string errorMessage, CancellationToken ct)
        => _pipelineDriver.HandleMergeFailureAsync(pipeline, errorMessage, ct);

    private Task DispatchToRole(GoalPipeline pipeline, WorkerRole role, string? prompt, CancellationToken ct)
        => _taskDispatchService.DispatchToRole(pipeline, role, prompt, ct);

    private Task SendAgentsMdToWorkerAsync(ConnectedWorker worker, WorkerRole role, CancellationToken ct)
        => _maintenance.SendAgentsMdToWorkerAsync(worker, role, ct);

    /// <summary>
    /// Sends an UpdateAgents message to all connected workers whose role matches the given role string.
    /// Best-effort: failures are logged but do not block the pipeline.
    /// </summary>
    private Task BroadcastAgentsUpdateAsync(WorkerRole role, string content, CancellationToken ct)
        => _maintenance.BroadcastAgentsUpdateAsync(role, content, ct);

    /// <summary>
    /// Pulls the latest config repo and broadcasts any AGENTS.md changes to connected workers.
    /// Best-effort: failures are logged but do not block the main dispatch loop.
    /// </summary>
    private Task SyncAgentsFromConfigRepoAsync(CancellationToken ct)
        => _maintenance.SyncAgentsFromConfigRepoAsync(ct);

    /// <summary>
    /// Restore active pipelines from the persistence store on startup.
    /// Re-primes Brain sessions and restores pipelines in the GoalPipelineManager.
    /// </summary>
    private Task RestoreActivePipelinesAsync(CancellationToken ct)
        => _maintenance.RestoreActivePipelinesAsync(ct);

    private Task CleanupOrphanedGoalSessionsAsync(CancellationToken ct)
        => _maintenance.CleanupOrphanedGoalSessionsAsync(ct);

    /// <summary>
    /// Drains the re-dispatch queue and dispatches the current phase for each
    /// queued pipeline. Called from the main poll loop — all dispatching happens here,
    /// avoiding race conditions from polling-based orphan detection.
    /// </summary>
    private Task DrainRedispatchQueueAsync(CancellationToken ct)
        => _goalDispatchService.DrainRedispatchQueueAsync(_redispatchQueue, ct);

    private Task DispatchNextGoalAsync(CancellationToken ct)
        => _goalDispatchService.DispatchNextGoalAsync(ct);

    internal List<TargetRepository> ResolveRepositories(Goal goal)
        => _taskDispatchService.ResolveRepositories(goal);

    private string BuildCoderPrompt(Goal goal)
    {
        return $"""
            You are a coder. Implement the following task. Start by reading the relevant source files, then make your code changes, build, test, and commit.

            Task: {goal.Description}

            Do NOT describe or plan changes — actually make them:
            1. Read the relevant source files
            2. Edit the files
            3. Use the build skill to build the project and fix any errors
            4. Use the test skill to run the tests and fix any failures
            5. Run `git add -A && git commit` with a descriptive message

            A response that only describes changes without actually editing files is a FAILURE.
            """;
    }

    /// <summary>
    /// Generates a commit message by asking the Brain for a concise summary first,
    /// falling back to <see cref="PipelineHelpers.BuildSquashCommitMessage"/> when the Brain is unavailable
    /// or returns null. The "Goal:" prefix is always preserved.
    /// </summary>
    internal async Task<string> GenerateMergeCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct)
    {
        if (_brain is null)
            return PipelineHelpers.BuildSquashCommitMessage(pipeline.GoalId, pipeline.Description);

        try
        {
            var brainMessage = await _brain.GenerateCommitMessageAsync(pipeline, ct);
            if (brainMessage is not null)
            {
                var prefix = $"Goal: {pipeline.GoalId} — ";
                return $"{prefix}{brainMessage}";
            }
        }
        catch (OperationCanceledException)
        {
            throw; // Preserve cancellation - do NOT swallow it
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate Brain commit message for goal {GoalId} — using fallback",
                pipeline.GoalId);
        }

        return PipelineHelpers.BuildSquashCommitMessage(pipeline.GoalId, pipeline.Description);
    }

}
