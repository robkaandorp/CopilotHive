using System.Collections.Concurrent;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Knowledge;
using CopilotHive.Orchestration;
using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Services;

/// <summary>
/// Handles selecting the next pending goal, creating its pipeline, planning the first
/// iteration and dispatching the first phase. Also drains the re-dispatch queue.
/// Extracted from <see cref="GoalDispatcher"/>.
/// </summary>
internal sealed class GoalDispatchService
{
    private readonly GoalManager _goalManager;
    private readonly GoalPipelineManager _pipelineManager;
    private readonly IDistributedBrain? _brain;
    private readonly HiveConfigFile? _config;
    private readonly TaskDispatchService _taskDispatchService;
    private readonly ClarificationHandler _clarificationHandler;
    private readonly KnowledgeGraph? _knowledgeGraph;
    private readonly IGoalStore? _goalStore;
    private readonly ConfigRepoManager? _configRepo;
    private readonly DashboardNotifier? _dashboardNotifier;
    private readonly ILogger _logger;
    private readonly IEventBus? _eventBus;

    /// <summary>
    /// Initialises a new <see cref="GoalDispatchService"/>.
    /// </summary>
    public GoalDispatchService(
        GoalManager goalManager,
        GoalPipelineManager pipelineManager,
        IDistributedBrain? brain,
        HiveConfigFile? config,
        TaskDispatchService taskDispatchService,
        ClarificationHandler clarificationHandler,
        KnowledgeGraph? knowledgeGraph,
        IGoalStore? goalStore,
        ConfigRepoManager? configRepo,
        DashboardNotifier? dashboardNotifier,
        ILogger logger,
        IEventBus? eventBus = null)
    {
        _goalManager = goalManager;
        _pipelineManager = pipelineManager;
        _brain = brain;
        _config = config;
        _taskDispatchService = taskDispatchService;
        _clarificationHandler = clarificationHandler;
        _knowledgeGraph = knowledgeGraph;
        _goalStore = goalStore;
        _configRepo = configRepo;
        _dashboardNotifier = dashboardNotifier;
        _logger = logger;
        _eventBus = eventBus;
    }

    /// <summary>
    /// Drains the re-dispatch queue and dispatches the current phase for each
    /// queued pipeline.
    /// </summary>
    public async Task DrainRedispatchQueueAsync(ConcurrentQueue<string> redispatchQueue, CancellationToken ct)
    {
        while (redispatchQueue.TryDequeue(out var goalId))
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
                ? await _clarificationHandler.ResolvePromptAsync(pipeline, pipeline.Phase,
                    "This task is being re-dispatched after the previous worker was lost. Continue from where the previous worker left off.",
                    ct)
                : $"Continue task for: {pipeline.Description}";

            await _taskDispatchService.DispatchToRole(pipeline, role.Value, prompt, ct);
        }
    }

    /// <summary>
    /// Selects the next pending goal (subject to the parallelism gate), creates its
    /// pipeline, plans the first iteration and dispatches the first phase.
    /// </summary>
    public async Task DispatchNextGoalAsync(CancellationToken ct)
    {
        // Parallelism gate: allow multiple goals to run concurrently when MaxParallelGoals > 1.
        // Each goal has its own Brain context with its own gate, so Brain LLM calls for different goals run in parallel.
        var maxParallel = _config?.Orchestrator?.MaxParallelGoals ?? 1;
        var activePipelines = _pipelineManager.GetActivePipelines();
        if (activePipelines.Count >= maxParallel)
            return;

        var goal = await _goalManager.GetNextGoalAsync(ct);
        if (goal is null)
            return;

        if (_pipelineManager.GetByGoalId(goal.Id) is not null)
            return;

        _logger.LogInformation("Dispatching goal '{GoalId}': {Description} (Priority={Priority})",
            goal.Id, goal.Description, goal.Priority);

        // Mark goal as in_progress IMMEDIATELY after selection, before any subsequent async work
        var startedMeta = new GoalUpdateMetadata { StartedAt = DateTime.UtcNow };
        _logger.LogInformation("Dispatcher: updating goal '{GoalId}' status to InProgress", goal.Id);
        try
        {
            await _goalManager.UpdateGoalStatusAsync(goal.Id, GoalStatus.InProgress, startedMeta, ct);
            goal.Status = GoalStatus.InProgress;
            if (startedMeta.StartedAt.HasValue)
                goal.StartedAt = startedMeta.StartedAt.Value;
            _dashboardNotifier?.NotifyStateChanged();
            _logger.LogInformation("Dispatcher: successfully updated goal '{GoalId}' status to InProgress", goal.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // propagate cancellation
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dispatcher: failed to update goal '{GoalId}' status to InProgress — continuing dispatch", goal.Id);
        }

        // Best-effort verification: re-read the goal to check if the status was persisted
        try
        {
            var verifyGoal = await _goalManager.GetGoalAsync(goal.Id, ct);
            if (verifyGoal is null)
                _logger.LogWarning("Dispatcher: verification returned null — goal '{GoalId}' not found after status update", goal.Id);
            else if (verifyGoal.Status != GoalStatus.InProgress)
                _logger.LogError("Dispatcher: VERIFICATION FAILED — goal '{GoalId}' DB status is {Status} after UpdateGoalStatusAsync(InProgress)", goal.Id, verifyGoal.Status);
            else
                _logger.LogInformation("Dispatcher: verified goal '{GoalId}' DB status is InProgress", goal.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dispatcher: failed to verify goal '{GoalId}' status (non-critical)", goal.Id);
        }

        // Ensure Brain repo clones are up-to-date before planning
        if (_brain is not null)
        {
            var repos = _taskDispatchService.ResolveRepositories(goal);
            foreach (var repo in repos)
            {
                try { await _brain.EnsureBrainRepoAsync(repo.Name, repo.Url, repo.DefaultBranch, ct); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to ensure Brain repo for '{RepoName}'", repo.Name);
                }
            }
        }

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

        // Plan iteration phases — planning failures fail the goal, never substitute a default plan
        IterationPlan iterationPlan;
        if (_brain is null)
        {
            // No brain — fail the goal immediately, no dispatch
            await FailNewGoalAsync(goal, pipeline, "No brain available for planning");
            return;
        }

        PlanResult planResult;
        try
        {
            planResult = await _clarificationHandler.ResolvePlanAsync(pipeline, null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Service shutdown — not a planning failure. Leave the goal alone and propagate.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the planning call itself (e.g. a Brain-side timeout) — fail the goal
            // gracefully rather than leaving a half-started pipeline behind.
            _logger.LogWarning("Planning was cancelled for goal '{GoalId}'", goal.Id);
            await FailNewGoalAsync(goal, pipeline, "Planning failed: planning was cancelled");
            return;
        }
        catch (Exception ex)
        {
            // A throw here leaves the pipeline half-started — fail the goal and clean up.
            _logger.LogError(ex, "Planning threw for goal '{GoalId}'", goal.Id);
            await FailNewGoalAsync(goal, pipeline, $"Planning failed: {ex.Message}");
            return;
        }

        if (planResult.IsFailed)
        {
            await FailNewGoalAsync(goal, pipeline, planResult.FailureReason!);
            return;
        }

        iterationPlan = planResult.Plan!;

        pipeline.SetPlan(iterationPlan);
        pipeline.StateMachine.StartIteration(iterationPlan.Phases);
        var firstPhase = iterationPlan.Phases[0];
        pipeline.AdvanceTo(firstPhase);

        // Create a living progress document in the knowledge graph for this goal.
        await CreateProgressDocumentAsync(goal, pipeline, iterationPlan, ct);

        // Craft prompt for first phase and dispatch
        var firstPhasePrompt = _brain is not null
            ? await _clarificationHandler.ResolvePromptAsync(pipeline, firstPhase, null, ct)
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

        var firstRole = firstPhase.ToWorkerRole();
        await _taskDispatchService.DispatchToRole(pipeline, firstRole, firstPhasePrompt, ct);

        // Only publish if a task was actually dispatched (ActiveTaskId is set).
        if (!string.IsNullOrEmpty(pipeline.ActiveTaskId))
            _eventBus?.Publish(new SystemEvent(
                Type: EventType.GoalDispatched,
                Message: pipeline.Description,
                GoalId: pipeline.GoalId));

        _pipelineManager.PersistFull(pipeline);
    }

    /// <summary>
    /// Fails a freshly-created goal whose iteration could not be planned. Marks the goal
    /// Failed with the reason, then independently attempts each cleanup step so a failure
    /// in one step never prevents the others. No worker is dispatched.
    /// </summary>
    /// <remarks>
    /// Every step uses <see cref="CancellationToken.None"/>: the caller's token may already be
    /// cancelled (that cancellation is often WHY planning failed), and cleanup must still run to
    /// completion. Using the caller's token here would abort the status write while still
    /// deleting the pipeline, stranding a persisted InProgress goal with no pipeline.
    /// The caller's token governs only the planning call itself, never this cleanup.
    /// </remarks>
    /// <param name="goal">The goal that could not be planned.</param>
    /// <param name="pipeline">The pipeline created for the goal.</param>
    /// <param name="failureReason">Why planning failed.</param>
    private async Task FailNewGoalAsync(Goal goal, GoalPipeline pipeline, string failureReason)
    {
        _logger.LogError("Failing goal '{GoalId}' — planning failed: {Reason}", goal.Id, failureReason);

        // Terminal state: both the pipeline phase and the state machine must agree.
        pipeline.StateMachine.Fail();
        pipeline.AdvanceTo(GoalPhase.Failed);

        // Step 1 (primary): mark the goal Failed in the store. If it fails, log and continue
        // with best-effort cleanup so no runtime state is left dangling.
        try
        {
            var meta = new GoalUpdateMetadata
            {
                CompletedAt = DateTime.UtcNow,
                Iterations = pipeline.Iteration,
                FailureReason = failureReason,
            };
            await _goalManager.UpdateGoalStatusAsync(goal.Id, GoalStatus.Failed, meta, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist Failed status for goal '{GoalId}'", goal.Id);
        }

        // Step 2 (best-effort): deregister the pipeline from the Brain.
        try
        {
            (_brain as DistributedBrain)?.DeregisterActivePipeline(goal.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deregister active pipeline for goal '{GoalId}'", goal.Id);
        }

        // Step 3 (best-effort): remove the pipeline from the manager and the durable store.
        try
        {
            _pipelineManager.RemovePipeline(goal.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove pipeline for goal '{GoalId}'", goal.Id);
        }

        // Step 4 (best-effort): delete the Brain goal session.
        if (_brain is not null)
        {
            try
            {
                await _brain.DeleteGoalSessionAsync(goal.Id, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete Brain goal session for goal '{GoalId}'", goal.Id);
            }
        }

        // Step 5 (best-effort): notify the dashboard.
        try
        {
            _dashboardNotifier?.NotifyStateChanged();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to notify dashboard for failed goal '{GoalId}'", goal.Id);
        }
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
}
