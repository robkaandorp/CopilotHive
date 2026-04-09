using System.Collections.Concurrent;
using CopilotHive.Agents;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Improvement;
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

    private readonly GoalManager _goalManager;
    private readonly GoalPipelineManager _pipelineManager;
    private readonly TaskQueue _taskQueue;
    private readonly IWorkerGateway _workerGateway;
    private readonly IDistributedBrain? _brain;
    private readonly IBrainRepoManager _repoManager;
    private readonly ILogger<GoalDispatcher> _logger;
    private readonly HiveConfigFile? _config;

    private readonly BranchCoordinator _branchCoordinator = new();
    private readonly TaskBuilder _taskBuilder = new(new BranchCoordinator());
    private readonly ConcurrentDictionary<string, bool> _dispatchedGoals = new();
    private readonly ConcurrentQueue<string> _redispatchQueue = new();
    private readonly TimeSpan _startupDelay;
    private readonly IClarificationRouter? _clarificationRouter;
    private readonly ClarificationQueueService? _clarificationQueue;
    private readonly ProgressLog? _progressLog;
    private readonly ClarificationHandler _clarificationHandler;
    private readonly GoalLifecycleService _lifecycleService;
    private readonly PipelineDriver _pipelineDriver;
    private readonly DispatcherMaintenance _maintenance;

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
        ProgressLog? progressLog = null)
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
        _clarificationHandler = new ClarificationHandler(brain, clarificationRouter, clarificationQueue, logger);

        _lifecycleService = new GoalLifecycleService(
            goalManager, logger, metricsTracker, agentsManager, configRepo, brain);

        _maintenance = new DispatcherMaintenance(
            pipelineManager, goalManager, taskQueue, workerGateway, brain,
            agentsManager, configRepo, _dispatchedGoals, _redispatchQueue, logger);

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
            logger: logger);

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
            _dispatchedGoals.TryRemove(goalId, out _);
            _logger.LogInformation("Goal {GoalId} cancelled (was InProgress, phase={Phase})", goalId, pipeline.Phase);
            if (_brain is not null)
                _brain.DeleteGoalSession(goalId);
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
        _dispatchedGoals.TryRemove(goalId, out _);
        _logger.LogInformation("Goal {GoalId} cancelled (was {Status})", goalId, goal.Status);
        if (_brain is not null)
            _brain.DeleteGoalSession(goalId);
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
    /// Clears all dispatcher runtime state for a goal that is being retried (Failed→Draft).
    /// Removes the goal from <see cref="_dispatchedGoals"/> so it can be dispatched again,
    /// and removes the stale pipeline from the <see cref="GoalPipelineManager"/> so the
    /// dispatcher does not see an active pipeline blocking new goal dispatch.
    /// </summary>
    /// <param name="goalId">The goal being retried.</param>
    public void ClearGoalRetryState(string goalId)
    {
        if (_brain is not null)
            _brain.DeleteGoalSession(goalId);
        _dispatchedGoals.TryRemove(goalId, out _);
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
    public async Task HandleTaskCompletionAsync(TaskResult result, CancellationToken ct = default)
    {
        var pipeline = _pipelineManager.GetByTaskId(result.TaskId);
        if (pipeline is null)
        {
            _logger.LogWarning("No pipeline found for completed task {TaskId}", result.TaskId);
            return;
        }

        // Guard: ignore late-arriving completions for goals already finished
        if (pipeline.Phase is GoalPhase.Done or GoalPhase.Failed)
        {
            _logger.LogInformation(
                "Task {TaskId} completed but goal {GoalId} already {Phase} — ignoring duplicate",
                result.TaskId, pipeline.GoalId, pipeline.Phase);
            return;
        }

        // Guard: ignore completions from tasks that are no longer the active task
        // (e.g., a stale task from a previous phase completing after the pipeline advanced)
        if (pipeline.ActiveTaskId is not null && pipeline.ActiveTaskId != result.TaskId)
        {
            _logger.LogWarning(
                "Task {TaskId} completed but pipeline {GoalId} active task is {ActiveTaskId} — ignoring stale completion",
                result.TaskId, pipeline.GoalId, pipeline.ActiveTaskId);
            return;
        }

        _logger.LogInformation("Pipeline {GoalId} task completed (phase={Phase}, status={Status}, model={Model})",
            pipeline.GoalId, pipeline.Phase, result.Status,
            string.IsNullOrEmpty(result.Model) ? "unknown" : result.Model);

        if (_brain is null)
        {
            await _lifecycleService.MarkGoalCompletedAsync(pipeline, ct);
            return;
        }

        try
        {
            await _pipelineDriver.DriveNextPhaseAsync(pipeline, result, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error driving pipeline {GoalId} to next phase", pipeline.GoalId);
            await _lifecycleService.MarkGoalFailedAsync(pipeline, ex.Message, ct);
        }

        _pipelineManager.PersistFull(pipeline);
    }

    // ── Forwarding wrappers for static methods (tests call GoalDispatcher.X directly) ──

    internal static IterationPlan ValidatePlan(IterationPlan plan)
        => IterationPlanValidator.ValidatePlan(plan);

    internal static string BuildPlanAdjustmentNote(List<GoalPhase> original, List<GoalPhase> final)
        => IterationPlanValidator.BuildPlanAdjustmentNote(original, final);

    // ── Forwarding wrappers for instance methods (tests call via reflection) ──

    private Task MarkGoalCompleted(GoalPipeline pipeline, CancellationToken ct)
        => _lifecycleService.MarkGoalCompletedAsync(pipeline, ct);

    private Task MarkGoalFailed(GoalPipeline pipeline, string reason, CancellationToken ct)
        => _lifecycleService.MarkGoalFailedAsync(pipeline, reason, ct);

    private Task HandleNewIterationAsync(GoalPipeline pipeline, string verdict, CancellationToken ct)
        => _pipelineDriver.HandleNewIterationAsync(pipeline, verdict, ct);

    private Task HandleMergeFailureAsync(GoalPipeline pipeline, string errorMessage, CancellationToken ct)
        => _pipelineDriver.HandleMergeFailureAsync(pipeline, errorMessage, ct);

    /// <summary>
    /// Builds a concise summary of worker output for the pipeline conversation.
    /// The Brain uses this to understand what each phase produced — especially
    /// WHY a reviewer rejected or what tests failed.
    /// </summary>
    internal static string BuildWorkerOutputSummary(GoalPhase phase, string verdict, TaskResult result)
        => PipelineHelpers.BuildWorkerOutputSummary(phase, verdict, result);

    /// <summary>
    /// Builds an <see cref="IterationSummary"/> from the pipeline's <see cref="GoalPipeline.PhaseLog"/>.
    /// Entries for the current iteration are extracted directly — no plan-walking or PhaseDurations needed.
    /// </summary>
    internal static IterationSummary BuildIterationSummary(GoalPipeline pipeline)
        => PipelineHelpers.BuildIterationSummary(pipeline);

    /// <summary>
    /// Builds a squash-merge commit message from a goal ID and description.
    /// </summary>
    internal static string BuildSquashCommitMessage(string goalId, string description)
        => PipelineHelpers.BuildSquashCommitMessage(goalId, description);

    internal static string InjectTokenIntoUrl(string url)
        => PipelineHelpers.InjectTokenIntoUrl(url);

    internal static string? GetLastCraftPromptFromConversation(GoalPipeline pipeline)
        => PipelineHelpers.GetLastCraftPromptFromConversation(pipeline);

    internal static (string? Prompt, string? Response) GetPlanningPromptsFromConversation(GoalPipeline pipeline)
        => PipelineHelpers.GetPlanningPromptsFromConversation(pipeline);

    private async Task DispatchToRole(GoalPipeline pipeline, WorkerRole role, string? prompt, CancellationToken ct)
    {
        prompt ??= $"Work on: {pipeline.Description}";

        // Log the prompt being sent to the worker
        var promptPreview = prompt.Length > 1500
            ? prompt[..1500] + $"... ({prompt.Length} chars total)"
            : prompt;
        _logger.LogDebug("Prompt for {Role} (goal={GoalId}):\n{Prompt}",
            role, pipeline.GoalId, promptPreview);

        var branchAction = pipeline.CoderBranch is null ? BranchAction.Create : BranchAction.Checkout;

        List<TargetRepository> repositories;
        try
        {
            repositories = ResolveRepositories(pipeline.Goal);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Repository configuration error for goal {GoalId}", pipeline.GoalId);
            await _lifecycleService.MarkGoalFailedAsync(pipeline, ex.Message, ct);
            return;
        }

        // Resolve per-role model from config; upgrade to premium when the Brain requested it for this phase
        var roleName = role.ToRoleName();
        var model = _config?.GetModelForRole(roleName);
        var currentPhase = pipeline.StateMachine.Phase;
        var phaseTier = pipeline.Plan?.PhaseTiers.GetValueOrDefault(currentPhase, ModelTier.Default) ?? ModelTier.Default;
        if (phaseTier == ModelTier.Premium && _config is not null)
        {
            var premiumModel = _config.GetPremiumModelForRole(roleName);
            if (premiumModel is not null)
                model = premiumModel;
        }
        _logger.LogDebug("Model for {Role}: {Model} (tier={Tier}, configLoaded={ConfigLoaded})",
            roleName, model ?? "(null)", phaseTier, _config is not null);

        // Resolve context window: per-role override > global worker default > constant fallback
        var maxContextTokens = _config?.GetContextWindowForRole(roleName) ?? Constants.DefaultBrainContextWindow;

        var task = _taskBuilder.Build(
            goalId: pipeline.GoalId,
            goalDescription: pipeline.Description,
            role: role,
            iteration: pipeline.Iteration,
            repositories: repositories,
            prompt: prompt,
            branchAction: branchAction,
            model: model,
            maxContextTokens: maxContextTokens);

        // Improver operates read-only: it can see the feature branch but must not push.
        // Downgrade the action to Unspecified so the worker runtime skips push operations.
        if (role == WorkerRole.Improver && task.BranchInfo is not null)
        {
            task.BranchInfo.Action = BranchAction.Unspecified;
        }

        // Propagate the iteration start SHA to the worker via metadata so reviewers can
        // compute an iteration-scoped diff alongside the cumulative branch diff.
        if (pipeline.IterationStartSha is not null)
            task.Metadata["iteration_start_sha"] = pipeline.IterationStartSha;

        // Propagate the tester's structured report to the reviewer so it can be retrieved via get_test_report.
        if (role == WorkerRole.Reviewer)
        {
            var testerEntry = pipeline.PhaseLog
                .LastOrDefault(e => e.Name == GoalPhase.Testing && e.Iteration == pipeline.Iteration && e.WorkerOutput is not null);
            if (testerEntry?.WorkerOutput is not null)
            {
                task.Metadata["tester_report"] = testerEntry.WorkerOutput;
            }
        }

        // Propagate compaction model to the worker so it creates a separate IChatClient for context compaction.
        var compactionModel = _config?.Models?.CompactionModel;
        if (!string.IsNullOrEmpty(compactionModel))
            task.Metadata["compaction_model"] = compactionModel;

        pipeline.SetActiveTask(task.TaskId, task.BranchInfo?.FeatureBranch);
        _pipelineManager.RegisterTask(task.TaskId, pipeline.GoalId);

        _taskQueue.Enqueue(task);
        _logger.LogInformation("Dispatched {Role} task {TaskId} for goal {GoalId} (branch={Branch})",
            role, task.TaskId, pipeline.GoalId, task.BranchInfo?.FeatureBranch);

        // Try to push directly to an idle worker
        var idleWorker = _workerGateway.GetIdleWorker();
        if (idleWorker is not null)
        {
            var queuedTask = _taskQueue.TryDequeue(role);
            queuedTask ??= _taskQueue.TryDequeueAny();

            if (queuedTask is not null)
            {
                idleWorker.Role = queuedTask.Role;
                var taskRoleName = queuedTask.Role.ToRoleName();
                _logger.LogInformation("Worker {WorkerId} assigned role {Role} for task {TaskId}",
                    idleWorker.Id, taskRoleName, queuedTask.TaskId);
                await SendAgentsMdToWorkerAsync(idleWorker, queuedTask.Role, ct);

                _taskQueue.Activate(queuedTask, idleWorker.Id);
                _workerGateway.MarkBusy(idleWorker.Id, queuedTask.TaskId);
                idleWorker.CurrentModel = queuedTask.Model;
                await _workerGateway.SendTaskAsync(idleWorker.Id, queuedTask, ct);
                _logger.LogInformation("Task {TaskId} pushed to worker {WorkerId}", queuedTask.TaskId, idleWorker.Id);
            }
        }
    }

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
    /// Re-primes Brain sessions and marks goals as dispatched.
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
    private async Task DrainRedispatchQueueAsync(CancellationToken ct)
    {
        while (_redispatchQueue.TryDequeue(out var goalId))
        {
            var pipeline = _pipelineManager.GetByGoalId(goalId);
            if (pipeline is null) continue;
            if (pipeline.ActiveTaskId is not null) continue;
            if (pipeline.Phase is GoalPhase.Done or GoalPhase.Failed) continue;

            var role = pipeline.Phase switch
            {
                GoalPhase.Coding => WorkerRole.Coder,
                GoalPhase.Testing => WorkerRole.Tester,
                GoalPhase.Review => WorkerRole.Reviewer,
                GoalPhase.DocWriting => WorkerRole.DocWriter,
                GoalPhase.Improve => WorkerRole.Improver,
                _ => (WorkerRole?)null,
            };

            if (role is null)
            {
                _logger.LogWarning("Pipeline {GoalId} queued for re-dispatch in phase {Phase} — no role mapping, skipping",
                    pipeline.GoalId, pipeline.Phase);
                continue;
            }

            _logger.LogInformation("Re-dispatching pipeline {GoalId} (phase={Phase}, role={Role})",
                pipeline.GoalId, pipeline.Phase, role);

            var prompt = _brain is not null
                ? await ResolvePromptAsync(pipeline, pipeline.Phase,
                    "This task is being re-dispatched after the previous worker was lost. Continue from where the previous worker left off.",
                    ct)
                : $"Continue task for: {pipeline.Description}";

            await DispatchToRole(pipeline, role.Value, prompt, ct);
        }
    }

    private async Task DispatchNextGoalAsync(CancellationToken ct)
    {
        // Parallelism gate: allow multiple goals to run concurrently when MaxParallelGoals > 1.
        // Each goal has its own Brain session, so the Brain's per-call gate (_brainCallGate)
        // still serializes individual Brain calls — parallelism is in worker execution.
        var maxParallel = _config?.Orchestrator?.MaxParallelGoals ?? 1;
        var activePipelines = _pipelineManager.GetActivePipelines();
        if (activePipelines.Count >= maxParallel)
            return;

        var goal = await _goalManager.GetNextGoalAsync(ct);
        if (goal is null)
            return;

        if (!_dispatchedGoals.TryAdd(goal.Id, true))
            return;

        _logger.LogInformation("Dispatching goal '{GoalId}': {Description} (Priority={Priority})",
            goal.Id, goal.Description, goal.Priority);

        // Ensure Brain repo clones are up-to-date before planning
        if (_brain is not null)
        {
            var repos = ResolveRepositories(goal);
            foreach (var repo in repos)
            {
                try { await _brain.EnsureBrainRepoAsync(repo.Name, repo.Url, repo.DefaultBranch, ct); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to ensure Brain repo for '{RepoName}'", repo.Name);
                }
            }
        }

        // Mark goal as in_progress with started_at timestamp
        var startedMeta = new GoalUpdateMetadata { StartedAt = DateTime.UtcNow };
        await _goalManager.UpdateGoalStatusAsync(goal.Id, GoalStatus.InProgress, startedMeta, ct);
        await _lifecycleService.CommitGoalsToConfigRepoAsync($"Goal '{goal.Id}' started", ct);

        // Create a pipeline for this goal
        var maxRetries = _config?.Orchestrator?.MaxRetriesPerTask ?? Constants.DefaultMaxRetriesPerTask;
        var maxIterations = _config?.Orchestrator?.MaxIterations ?? Constants.DefaultMaxIterations;
        var pipeline = _pipelineManager.CreatePipeline(goal, maxRetries, maxIterations);
        pipeline.GoalStartedAt = startedMeta.StartedAt;

        // Register with Brain so the get_goal tool can return live iteration/phase info
        (_brain as DistributedBrain)?.RegisterActivePipeline(pipeline);

        // Fork a per-goal Brain session from the master so this goal's context
        // is isolated from other concurrent goals.
        if (_brain is not null)
        {
            await _brain.ForkSessionForGoalAsync(goal.Id, ct);
        }

        // Plan iteration phases
        IterationPlan iterationPlan;
        if (_brain is not null)
        {
            try
            {
                var rawPlan = await ResolvePlanAsync(pipeline, null, ct);
                var originalPhases = rawPlan.Phases.ToList();
                iterationPlan = ValidatePlan(rawPlan);

                if (!originalPhases.SequenceEqual(iterationPlan.Phases))
                {
                    var note = BuildPlanAdjustmentNote(originalPhases, iterationPlan.Phases);
                    await _brain.InjectSystemNoteAsync(pipeline, note, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Brain failed to plan iteration for {GoalId}, using default plan", pipeline.GoalId);
                iterationPlan = IterationPlan.Default();
            }
        }
        else
        {
            iterationPlan = IterationPlan.Default();
        }

        pipeline.SetPlan(iterationPlan);
        pipeline.StateMachine.StartIteration(iterationPlan.Phases);
        var firstPhase = iterationPlan.Phases[0];
        pipeline.AdvanceTo(firstPhase);

        // Craft prompt for first phase and dispatch
        var firstPhasePrompt = _brain is not null
            ? await ResolvePromptAsync(pipeline, firstPhase, null, ct)
            : (firstPhase == GoalPhase.Coding ? BuildCoderPrompt(goal) : $"Work on: {pipeline.Description}");

        // PhaseLog: append entry for the first phase of the pipeline
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = firstPhase,
            Result = PhaseOutcome.Pass,
            Occurrence = 1,
            Iteration = pipeline.Iteration,
            StartedAt = DateTime.UtcNow,
        });
        if (pipeline.CurrentPhaseEntry is { } firstPhaseEntry)
        {
            firstPhaseEntry.WorkerPrompt = firstPhasePrompt;
            firstPhaseEntry.BrainPrompt = GetLastCraftPromptFromConversation(pipeline);
            // Capture planning prompt/response from conversation onto the first entry
            var (planningPrompt, planningResponse) = GetPlanningPromptsFromConversation(pipeline);
            firstPhaseEntry.PlanningPrompt = planningPrompt;
            firstPhaseEntry.PlanningResponse = planningResponse;
        }

        var firstRole = firstPhase.ToWorkerRole();
        await DispatchToRole(pipeline, firstRole, firstPhasePrompt, ct);

        _pipelineManager.PersistFull(pipeline);
    }

    /// <summary>
    /// Resolves the list of <see cref="TargetRepository"/> instances for the given goal by looking
    /// up each repository name in the hive configuration.
    /// </summary>
    /// <param name="goal">The goal whose <see cref="Goal.RepositoryNames"/> are to be resolved.</param>
    /// <returns>A list of resolved <see cref="TargetRepository"/> objects with injected credentials.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when any repository name referenced by the goal is not defined in hive-config.yaml.
    /// </exception>
    internal List<TargetRepository> ResolveRepositories(Goal goal)
    {
        var repos = new List<TargetRepository>();

        foreach (var repoName in goal.RepositoryNames)
        {
            var repoConfig = _config?.Repositories.FirstOrDefault(
                r => r.Name.Equals(repoName, StringComparison.OrdinalIgnoreCase));

            if (repoConfig is not null)
            {
                var url = InjectTokenIntoUrl(repoConfig.Url);
                repos.Add(new TargetRepository
                {
                    Name = repoConfig.Name,
                    Url = url,
                    DefaultBranch = repoConfig.DefaultBranch,
                });
            }
            else
            {
                throw new InvalidOperationException(
                    $"Goal '{goal.Id}' references repository '{repoName}' which is not defined in hive-config.yaml. Add it to the repositories section or remove it from the goal.");
            }
        }

        return repos;
    }

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
    /// falling back to <see cref="BuildSquashCommitMessage"/> when the Brain is unavailable
    /// or returns null. The "Goal:" prefix is always preserved.
    /// </summary>
    internal async Task<string> GenerateMergeCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct)
    {
        if (_brain is null)
            return BuildSquashCommitMessage(pipeline.GoalId, pipeline.Description);

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

        return BuildSquashCommitMessage(pipeline.GoalId, pipeline.Description);
    }

}
