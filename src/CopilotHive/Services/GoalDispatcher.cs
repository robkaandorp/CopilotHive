using System.Collections.Concurrent;
using CopilotHive.Agents;
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
    private DateTime _lastBranchCleanup = DateTime.MinValue;

    private int _dispatchingCount;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _dispatchCancellations = new();
    private readonly object _brainRegistryLock = new();
    private readonly object _dispatchTasksLock = new();
    private readonly List<Task> _dispatchTasks = new();

    /// <summary>
    /// Exposes the most recently launched background dispatch task for test synchronization.
    /// </summary>
    internal Task? LastDispatchTask { get; private set; }

    /// <summary>
    /// Tracks the progression of a single goal dispatch through its lifecycle so cleanup
    /// can determine exactly how far dispatch got and what to roll back.
    /// </summary>
    internal enum DispatchState
    {
        Reserved,          // goal selected, _dispatchedGoals added, status InProgress, CTS created
        PipelineCreated,   // pipeline exists, _dispatchingCount decremented
        TaskEnqueued,      // DispatchToRole enqueued the task (ActiveTaskId set)
        TaskSent,          // task sent to an idle worker
        Completed          // dispatch finished successfully
    }

    /// <summary>
    /// Describes the outcome of <see cref="DispatchToRole"/> so callers can react appropriately.
    /// </summary>
    internal enum DispatchOutcome
    {
        FailedBeforeEnqueue,   // repository error or pre-enqueue exception
        QueuedNoWorker,        // enqueued, no idle worker available
        DequeuedButUnsent,     // dequeued for delivery, exception before SendTaskAsync
        SentToWorker           // task sent to idle worker
    }

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
        IGoalStore? goalStore = null)
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
        _clarificationHandler = new ClarificationHandler(brain, clarificationRouter, clarificationQueue, logger);

        _lifecycleService = new GoalLifecycleService(
            goalManager, logger, metricsTracker, agentsManager, configRepo, brain);

        _maintenance = new DispatcherMaintenance(
            pipelineManager, goalManager, taskQueue, workerGateway, brain,
            agentsManager, configRepo, _dispatchedGoals, _redispatchQueue, logger,
            knowledgeGraph,
            goalStore: goalStore,
            repoManager: repoManager,
            config: config);

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
        var hasDispatch = _dispatchCancellations.ContainsKey(goalId);
        var pipeline = _pipelineManager.GetByGoalId(goalId);

        // Cancel any in-flight dispatch so DispatchGoalAsync unwinds cleanly.
        if (hasDispatch && _dispatchCancellations.TryGetValue(goalId, out var dispatchCts))
        {
            try { dispatchCts.Cancel(); } catch { }
        }

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

        if (hasDispatch)
        {
            // Dispatch is in-flight but no pipeline exists yet — the CTS cancel above will
            // trigger cleanup inside DispatchGoalAsync. Remove the dispatch guard so the goal
            // is not considered still-dispatched.
            _dispatchedGoals.TryRemove(goalId, out _);
            _logger.LogInformation("Goal {GoalId} cancelled (dispatch in-flight, no pipeline yet)", goalId);
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

        try
        {
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
        }
        finally
        {
            // Cancel all in-flight dispatch CTS entries
            foreach (var kvp in _dispatchCancellations)
            {
                try { kvp.Value.Cancel(); } catch { }
            }

            // Await remaining dispatch tasks with a 30s timeout
            List<Task> tasksToAwait;
            lock (_dispatchTasksLock)
            {
                tasksToAwait = _dispatchTasks.Where(t => !t.IsCompleted).ToList();
            }

            if (tasksToAwait.Count > 0)
            {
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None);
                var allTasksTask = Task.WhenAll(tasksToAwait);
                var completed = await Task.WhenAny(allTasksTask, timeoutTask);
                if (completed == timeoutTask)
                    _logger.LogWarning("Timeout waiting for {Count} dispatch tasks during shutdown", tasksToAwait.Count);

                // Observe exceptions to prevent unobserved task exceptions
                foreach (var t in tasksToAwait)
                {
                    try { await t; } catch { }
                }
            }

            // Prune completed tasks
            lock (_dispatchTasksLock)
            {
                _dispatchTasks.RemoveAll(t => t.IsCompleted);
            }

            _logger.LogInformation("GoalDispatcher stopped");
        }
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

    // ── Forwarding wrappers for instance methods (tests call via reflection) ──

    private Task MarkGoalCompleted(GoalPipeline pipeline, CancellationToken ct)
        => _lifecycleService.MarkGoalCompletedAsync(pipeline, ct);

    private Task MarkGoalFailed(GoalPipeline pipeline, string reason, CancellationToken ct)
        => _lifecycleService.MarkGoalFailedAsync(pipeline, reason, ct);

    private Task HandleNewIterationAsync(GoalPipeline pipeline, string verdict, CancellationToken ct)
        => _pipelineDriver.HandleNewIterationAsync(pipeline, verdict, ct);

    private Task HandleMergeFailureAsync(GoalPipeline pipeline, string errorMessage, CancellationToken ct)
        => _pipelineDriver.HandleMergeFailureAsync(pipeline, errorMessage, ct);

    private async Task<DispatchOutcome> DispatchToRole(GoalPipeline pipeline, WorkerRole role, string? prompt, CancellationToken ct)
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
            return DispatchOutcome.FailedBeforeEnqueue;
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

        // Apply configured reasoning effort as a model suffix (explicit :suffix takes precedence)
        if (_config is not null && model is not null)
        {
            var reasoningEffort = _config.TryGetReasoningEffortForModel(model);
            model = HiveConfigFile.ApplyReasoningSuffix(model, reasoningEffort);
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
        {
            var compactionCtx = _config?.TryGetContextWindowForModel(compactionModel);

            // Apply configured reasoning effort as a model suffix (explicit :suffix takes precedence)
            var compactionReasoningEffort = _config?.TryGetReasoningEffortForModel(compactionModel);
            compactionModel = HiveConfigFile.ApplyReasoningSuffix(compactionModel, compactionReasoningEffort);

            task.Metadata["compaction_model"] = compactionModel;
            if (compactionCtx is int ctx && ctx > 0)
                task.Metadata["compaction_max_tokens"] = ctx.ToString();
        }

        pipeline.SetActiveTask(task.TaskId, task.BranchInfo?.FeatureBranch);
        _pipelineManager.RegisterTask(task.TaskId, pipeline.GoalId);

        _taskQueue.Enqueue(task);
        _logger.LogInformation("Dispatched {Role} task {TaskId} for goal {GoalId} (branch={Branch})",
            role, task.TaskId, pipeline.GoalId, task.BranchInfo?.FeatureBranch);

        // Try to push directly to an idle worker
        var idleWorker = _workerGateway.GetIdleWorker();
        if (idleWorker is null)
            return DispatchOutcome.QueuedNoWorker;

        var queuedTask = _taskQueue.TryDequeue(role);
        queuedTask ??= _taskQueue.TryDequeueAny();

        if (queuedTask is null)
            return DispatchOutcome.QueuedNoWorker;

        try
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
            return DispatchOutcome.SentToWorker;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send task {TaskId} to worker {WorkerId} — re-enqueuing",
                queuedTask.TaskId, idleWorker.Id);
            // Re-enqueue so a worker can pick it up later.
            _taskQueue.Enqueue(queuedTask);
            return DispatchOutcome.DequeuedButUnsent;
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

        // Prune completed dispatch tasks so the list does not grow unbounded.
        lock (_dispatchTasksLock)
        {
            _dispatchTasks.RemoveAll(t => t.IsCompleted);
        }

        while (true)
        {
            var activePipelines = _pipelineManager.GetActivePipelines();
            if (activePipelines.Count + Volatile.Read(ref _dispatchingCount) >= maxParallel)
                return;

            var goal = await _goalManager.GetNextGoalAsync(ct);
            if (goal is null)
                return;

            // Reserve synchronously BEFORE launching any async work.
            if (!_dispatchedGoals.TryAdd(goal.Id, true))
                return;

            Interlocked.Increment(ref _dispatchingCount);

            var dispatchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (!_dispatchCancellations.TryAdd(goal.Id, dispatchCts))
            {
                ReleaseReservation(goal.Id, dispatchCts, pipelineCreated: false);
                return;
            }

            _logger.LogInformation("Dispatching goal '{GoalId}': {Description} (Priority={Priority})",
                goal.Id, goal.Description, goal.Priority);

            // Mark goal as in_progress with started_at timestamp BEFORE launching background work
            // so the dashboard reflects InProgress immediately.
            var startedMeta = new GoalUpdateMetadata { StartedAt = DateTime.UtcNow };
            try
            {
                await _goalManager.UpdateGoalStatusAsync(goal.Id, GoalStatus.InProgress, startedMeta, ct);
            }
            catch (Exception ex)
            {
                // If the goal is already InProgress we can proceed; otherwise roll back and fail.
                Goal? current = null;
                try { current = await _goalManager.GetGoalAsync(goal.Id, ct); } catch { }
                if (current?.Status != GoalStatus.InProgress)
                {
                    _logger.LogError(ex, "Failed to mark goal '{GoalId}' InProgress — aborting dispatch", goal.Id);
                    ReleaseReservation(goal.Id, dispatchCts, pipelineCreated: false);
                    try
                    {
                        await _goalManager.UpdateGoalStatusAsync(goal.Id, GoalStatus.Failed,
                            new GoalUpdateMetadata { FailureReason = "Dispatch failed" }, CancellationToken.None);
                    }
                    catch (Exception failEx)
                    {
                        _logger.LogWarning(failEx, "Failed to mark goal {GoalId} as Failed", goal.Id);
                    }
                    return;
                }
            }

            try
            {
                await _lifecycleService.CommitGoalsToConfigRepoAsync($"Goal '{goal.Id}' started", ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to commit goals to config repo for goal {GoalId}", goal.Id);
            }

            // Launch the heavy dispatch work on a background task so multiple goals can proceed
            // in parallel. Pass the dispatch token to DispatchGoalAsync (not to Task.Run) so the
            // delegate always runs and the finally block can perform cleanup.
            var task = Task.Run(() => DispatchGoalAsync(goal, dispatchCts, startedMeta), CancellationToken.None);
            LastDispatchTask = task;
            lock (_dispatchTasksLock)
            {
                _dispatchTasks.Add(task);
            }
        }
    }

    /// <summary>
    /// Releases a dispatch reservation. Removes the (value-matched) CTS, then the
    /// <see cref="_dispatchedGoals"/> entry, then decrements <see cref="_dispatchingCount"/>
    /// when the pipeline was not created (if it was, the count was already decremented).
    /// This is the single cleanup helper for a dispatch reservation.
    /// </summary>
    private void ReleaseReservation(string goalId, CancellationTokenSource? cts, bool pipelineCreated)
    {
        // 1. Remove CTS (value-matched to avoid removing another dispatch's CTS)
        if (cts is not null && _dispatchCancellations.TryRemove(new KeyValuePair<string, CancellationTokenSource>(goalId, cts)))
            cts.Dispose();
        // 2. Remove _dispatchedGoals (after CTS is safely removed — prevents same-goal retry)
        _dispatchedGoals.TryRemove(goalId, out _);
        // 3. Decrement count if pipeline wasn't created (if it was, count was already decremented)
        if (!pipelineCreated)
            Interlocked.Decrement(ref _dispatchingCount);
    }

    /// <summary>
    /// Runs the heavy dispatch workflow for a single goal on a background task: ensures Brain repos,
    /// creates the pipeline, registers with the Brain, forks the session, plans the iteration, crafts
    /// the first prompt, and dispatches the first phase. Implements a dispatch state machine so cleanup
    /// can roll back exactly the work that was performed. The <c>finally</c> block always calls
    /// <see cref="ReleaseReservation"/>.
    /// </summary>
    private async Task DispatchGoalAsync(Goal goal, CancellationTokenSource dispatchCts, GoalUpdateMetadata startedMeta)
    {
        var ct = dispatchCts.Token;
        var state = DispatchState.Reserved;
        try
        {
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

            ct.ThrowIfCancellationRequested();

            // Create a pipeline for this goal
            var maxRetries = _config?.Orchestrator?.MaxRetriesPerTask ?? Constants.DefaultMaxRetriesPerTask;
            var maxIterations = _config?.Orchestrator?.MaxIterations ?? Constants.DefaultMaxIterations;
            var pipeline = _pipelineManager.CreatePipeline(goal, maxRetries, maxIterations);
            pipeline.GoalStartedAt = startedMeta.StartedAt;

            Interlocked.Decrement(ref _dispatchingCount);
            state = DispatchState.PipelineCreated;

            // Register with Brain under lock (DistributedBrain uses a non-concurrent Dictionary)
            lock (_brainRegistryLock)
            {
                (_brain as DistributedBrain)?.RegisterActivePipeline(pipeline);
            }

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
                    iterationPlan = IterationPlanValidator.ValidatePlan(rawPlan);

                    if (!originalPhases.SequenceEqual(iterationPlan.Phases))
                    {
                        var note = IterationPlanValidator.BuildPlanAdjustmentNote(originalPhases, iterationPlan.Phases);
                        await _brain.InjectSystemNoteAsync(pipeline, note, ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // Re-throw cancellation — caught by outer catch
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

            // Create a living progress document in the knowledge graph for this goal.
            await CreateProgressDocumentAsync(goal, pipeline, iterationPlan, ct);

            // Craft prompt for first phase and dispatch
            var firstPhasePrompt = _brain is not null
                ? await ResolvePromptAsync(pipeline, firstPhase, null, ct)
                : (firstPhase == GoalPhase.Coding ? BuildCoderPrompt(goal) : $"Work on: {pipeline.Description}");

            // PhaseLog: append entry for the first phase of the pipeline
            pipeline.PhaseLog.Add(PhaseResult.Create(firstPhase, pipeline.Iteration, 1));
            if (pipeline.CurrentPhaseEntry is { } firstPhaseEntry)
            {
                firstPhaseEntry.WorkerPrompt = firstPhasePrompt;
                firstPhaseEntry.BrainPrompt = PipelineHelpers.GetLastCraftPromptFromConversation(pipeline);
                // Capture planning prompt/response from conversation onto the first entry
                var (planningPrompt, planningResponse) = PipelineHelpers.GetPlanningPromptsFromConversation(pipeline);
                firstPhaseEntry.PlanningPrompt = planningPrompt;
                firstPhaseEntry.PlanningResponse = planningResponse;
            }

            ct.ThrowIfCancellationRequested();

            var firstRole = firstPhase.ToWorkerRole();
            var outcome = await DispatchToRole(pipeline, firstRole, firstPhasePrompt, ct);

            if (outcome == DispatchOutcome.FailedBeforeEnqueue)
            {
                await HandleDispatchFailureAsync(goal.Id);
                return; // finally calls ReleaseReservation
            }

            state = outcome == DispatchOutcome.SentToWorker ? DispatchState.TaskSent : DispatchState.TaskEnqueued;

            _pipelineManager.PersistFull(pipeline);
            state = DispatchState.Completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Dispatch of '{GoalId}' canceled", goal.Id);
            if (state >= DispatchState.PipelineCreated)
                await HandleDispatchFailureAsync(goal.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dispatching '{GoalId}'", goal.Id);
            if (state >= DispatchState.PipelineCreated && state < DispatchState.TaskEnqueued)
                await HandleDispatchFailureAsync(goal.Id);
            // If TaskEnqueued or TaskSent, preserve pipeline (worker correlation)
        }
        finally
        {
            ReleaseReservation(goal.Id, dispatchCts, pipelineCreated: state >= DispatchState.PipelineCreated);
        }
    }

    /// <summary>
    /// Rolls back the Brain registration, pipeline, and goal session for a failed dispatch, then marks
    /// the goal Failed. Idempotent — safe to call more than once (e.g., cancel then catch). Does NOT
    /// remove <see cref="_dispatchedGoals"/> or the CTS; the caller's <c>finally</c> block
    /// (<see cref="ReleaseReservation"/>) handles those.
    /// </summary>
    private async Task HandleDispatchFailureAsync(string goalId)
    {
        var pipeline = _pipelineManager.GetByGoalId(goalId);

        lock (_brainRegistryLock)
        {
            (_brain as DistributedBrain)?.DeregisterActivePipeline(goalId);
        }

        _pipelineManager.RemovePipeline(goalId);

        if (_brain is not null)
            try { _brain.DeleteGoalSession(goalId); } catch { }

        if (pipeline is not null)
        {
            try { await _lifecycleService.MarkGoalFailedAsync(pipeline, "Dispatch failed", CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to mark goal {GoalId} as Failed", goalId); }
        }
        else
        {
            try
            {
                await _goalManager.UpdateGoalStatusAsync(goalId, GoalStatus.Failed,
                    new GoalUpdateMetadata { FailureReason = "Dispatch failed" }, CancellationToken.None);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to mark goal {GoalId} as Failed", goalId); }
        }
    }

    /// <summary>
    /// Synchronous dispatch wrapper for test compatibility. Runs <see cref="DispatchNextGoalAsync"/>
    /// and then awaits the launched background dispatch task so tests observe the full result.
    /// </summary>
    internal async Task DispatchNextGoalAsyncSync(CancellationToken ct)
    {
        await DispatchNextGoalAsync(ct);
        if (LastDispatchTask is not null)
            await LastDispatchTask;
    }


    /// <summary>
    /// Creates a living progress document in the knowledge graph for the given goal, links it to the
    /// goal via the <see cref="Goal.Documents"/> field, and appends the Brain's initial iteration plan.
    /// Failures are logged and swallowed — the progress document is best-effort and never blocks dispatch.
    /// </summary>
    private async Task CreateProgressDocumentAsync(Goal goal, GoalPipeline pipeline, IterationPlan iterationPlan, CancellationToken ct)
    {
        if (_knowledgeGraph is null)
            return;

        var docId = $"progress-{goal.Id}";
        try
        {
            var title = $"Progress: {goal.Id}";
            var headerContent = $"# {title}\n";

            await _knowledgeGraph.CreateDocumentAsync(
                id: docId,
                title: title,
                type: DocumentType.Scratch,
                content: headerContent,
                topic: "progress",
                ct: ct);

            // Link the document to the goal via the documents field
            if (_goalStore is not null && !goal.Documents.Contains(docId))
            {
                goal.Documents.Add(docId);
                await _goalStore.UpdateGoalAsync(goal, ct);
            }

            // Append the Brain's initial iteration plan
            var planText = PipelineProgressFormatting.BuildPlanSection(pipeline.Iteration, iterationPlan);
            var doc = _knowledgeGraph.GetDocument(docId);
            if (doc is not null)
            {
                var newContent = doc.Content.TrimEnd() + "\n\n" + planText;
                await _knowledgeGraph.UpdateDocumentAsync(docId, content: newContent, ct: ct);
            }

            if (_configRepo is not null)
                await _knowledgeGraph.CommitToConfigRepoAsync(_configRepo.LocalPath, $"Create progress document: {docId}", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create progress document for goal {GoalId}", goal.Id);
        }
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
                var url = PipelineHelpers.InjectTokenIntoUrl(repoConfig.Url);
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
