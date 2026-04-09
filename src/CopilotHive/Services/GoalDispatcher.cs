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
    private readonly ImprovementAnalyzer? _improvementAnalyzer;
    private readonly AgentsManager? _agentsManager;
    private readonly MetricsTracker? _metricsTracker;
    private readonly ConfigRepoManager? _configRepo;
    private readonly IBrainRepoManager _repoManager;
    private readonly ILogger<GoalDispatcher> _logger;
    private readonly HiveConfigFile? _config;

    private readonly BranchCoordinator _branchCoordinator = new();
    private readonly TaskBuilder _taskBuilder = new(new BranchCoordinator());
    private readonly ConcurrentDictionary<string, bool> _dispatchedGoals = new();
    private readonly ConcurrentQueue<string> _redispatchQueue = new();
    private DateTime _lastAgentsSync = DateTime.MinValue;
    private readonly TimeSpan _startupDelay;
    private readonly IClarificationRouter? _clarificationRouter;
    private readonly ClarificationQueueService? _clarificationQueue;
    private readonly ProgressLog? _progressLog;
    private readonly ClarificationHandler _clarificationHandler;

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
        _improvementAnalyzer = improvementAnalyzer;
        _agentsManager = agentsManager;
        _metricsTracker = metricsTracker;
        _logger = logger;
        _config = config;
        _configRepo = configRepo;
        _clarificationRouter = clarificationRouter;
        _clarificationQueue = clarificationQueue;
        _startupDelay = startupDelay ?? TimeSpan.FromSeconds(10);
        _progressLog = progressLog;
        _clarificationHandler = new ClarificationHandler(brain, clarificationRouter, clarificationQueue, logger);

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

            await MarkGoalFailed(pipeline, "Cancelled by user", ct);
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
                if (DateTime.UtcNow - _lastAgentsSync > AgentsSyncInterval)
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
            await MarkGoalCompleted(pipeline, ct);
            return;
        }

        try
        {
            await DriveNextPhaseAsync(pipeline, result, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error driving pipeline {GoalId} to next phase", pipeline.GoalId);
            await MarkGoalFailed(pipeline, ex.Message, ct);
        }

        _pipelineManager.PersistFull(pipeline);
    }

    private async Task DriveNextPhaseAsync(GoalPipeline pipeline, TaskResult result, CancellationToken ct)
    {
        // Early-exit guard: a crashed/failed worker should not continue through the pipeline.
        // Recording the crash output as normal output would pollute iteration data.
        if (result.Status == TaskOutcome.Failed)
        {
            var truncatedOutput = result.Output.Length > 300 ? result.Output[..300] + "..." : result.Output;
            _logger.LogError("Worker for goal {GoalId} failed with output: {Output}", pipeline.GoalId, result.Output);
            await MarkGoalFailed(pipeline, $"Worker failed: {truncatedOutput}", ct);
            return;
        }

        // Extract the iteration-start SHA from coder results so the reviewer can later compute
        // a scoped diff (git diff {sha}..HEAD) showing only this iteration's changes.
        // The SHA comes from the worker's feature-branch clone, which is correct — unlike the
        // Brain's persistent clone which stays on the default branch between iterations.
        if (pipeline.Phase == GoalPhase.Coding && !string.IsNullOrEmpty(result.IterationStartSha))
        {
            pipeline.IterationStartSha = result.IterationStartSha;
            _logger.LogDebug("Stored iteration start SHA {Sha} for goal {GoalId} (from coder task result)",
                result.IterationStartSha[..Math.Min(result.IterationStartSha.Length, 12)], pipeline.GoalId);
        }

        // No-op detection: if the coder returned without making any file changes,
        // skip verdict extraction and immediately retry with a stronger prompt.
        if (pipeline.Phase == GoalPhase.Coding && (result.GitStatus?.FilesChanged ?? 0) == 0)
        {
            _logger.LogWarning(
                "No-op detected: Coder for {GoalId} returned with 0 files changed — retrying with stronger prompt",
                pipeline.GoalId);

            if (!pipeline.IterationBudget.TryConsume())
            {
                await MarkGoalFailed(pipeline, "Coder produced no file changes after max iterations (no-op)", ct);
                return;
            }

            var prevContext = !string.IsNullOrWhiteSpace(result.Metrics?.Summary)
                ? result.Metrics.Summary
                : (result.Output.Length > 500 ? result.Output[..500] + "..." : result.Output);
            var noOpContext =
                "CRITICAL: Your previous attempt produced ZERO file changes. " +
                "You MUST edit files and commit them with `git add -A && git commit`. " +
                "Do NOT just describe or discuss changes — actually make them.\n\n" +
                $"Previous coder context:\n{prevContext}";

            var retryPrompt = _brain is not null
                ? await ResolvePromptAsync(pipeline, GoalPhase.Coding, noOpContext, ct)
                : $"Work on: {pipeline.Description}. {noOpContext}";
            await DispatchToRole(pipeline, WorkerRole.Coder, retryPrompt, ct);
            return;
        }

        // Log the raw worker output — critical for debugging
        var outputPreview = result.Output.Length > 2000
            ? result.Output[..2000] + $"... ({result.Output.Length} chars total)"
            : result.Output;
        _logger.LogInformation(
            "Worker output for {GoalId} (phase={Phase}):\n{Output}",
            pipeline.GoalId, pipeline.Phase, outputPreview);

        if (result.GitStatus is { FilesChanged: > 0, Pushed: false })
            _logger.LogWarning(
                "Task {TaskId} had {Files} file changes but push failed",
                result.TaskId, result.GitStatus.FilesChanged);

        // Extract structured verdict from worker tool call metrics
        var verdict = Verdict.Pass; // Default: worker completed successfully
        if (result.Metrics is { } metrics)
        {
            if (!string.IsNullOrEmpty(metrics.Verdict))
                verdict = metrics.Verdict;

            // Populate pipeline metrics from structured data
            if (pipeline.Phase == GoalPhase.Testing && metrics.TotalTests > 0)
            {
                pipeline.Metrics.TotalTests = metrics.TotalTests;
                pipeline.Metrics.PassedTests = metrics.PassedTests;
                pipeline.Metrics.FailedTests = metrics.FailedTests;
                pipeline.Metrics.BuildSuccess = metrics.BuildSuccess;
                if (metrics.CoveragePercent > 0)
                    pipeline.Metrics.CoveragePercent = metrics.CoveragePercent;

                _logger.LogInformation(
                    "Structured test metrics for {GoalId}: {Passed}/{Total} passed, {Failed} failed, verdict={Verdict}",
                    pipeline.GoalId, metrics.PassedTests, metrics.TotalTests, metrics.FailedTests, metrics.Verdict);
            }

            if (pipeline.Phase == GoalPhase.Review && (Verdict.Matches(verdict, Verdict.Approve) || Verdict.Matches(verdict, Verdict.RequestChanges)))
            {
                pipeline.Metrics.ReviewVerdict = ReviewVerdictExtensions.ParseReviewVerdict(verdict);
                if (metrics.Issues is { Count: > 0 })
                {
                    pipeline.Metrics.ReviewIssuesFound += metrics.Issues.Count;
                    pipeline.Metrics.ReviewIssues.AddRange(metrics.Issues);
                }

                _logger.LogInformation(
                    "Structured review verdict for {GoalId}: {Verdict}, {IssueCount} issues",
                    pipeline.GoalId, verdict, metrics.Issues?.Count ?? 0);
            }

            if (metrics.Issues is not null)
                pipeline.Metrics.Issues.AddRange(metrics.Issues);
        }

        // Record worker output in the conversation so the Brain sees it when replanning.
        // This is critical: without this, the Brain knows "2 review retries" but not WHY
        // the reviewer rejected. Use a structured summary to stay within token budget.
        var workerRole = pipeline.Phase.ToWorkerRole().ToRoleName();
        var outputSummary = BuildWorkerOutputSummary(pipeline.Phase, verdict, result);
        pipeline.Conversation.Add(new ConversationEntry(workerRole, outputSummary, pipeline.Iteration, "worker-output"));

        // After Improver: sync config repo to pick up the changes it pushed directly
        if (pipeline.Phase == GoalPhase.Improve)
        {
            _logger.LogInformation("Improver completed for goal {GoalId} — syncing config repo for updated agents.md files",
                pipeline.GoalId);
            await SyncAgentsFromConfigRepoAsync(ct);
        }

        // Map verdict to PhaseInput directly — no Brain interpretation needed
        var phaseInput = pipeline.Phase == GoalPhase.Improve
            ? PhaseInput.Succeeded // Improve phase is non-blocking
            : verdict switch
            {
                var v when Verdict.Matches(v, Verdict.Fail) || Verdict.Matches(v, Verdict.Cancelled) => PhaseInput.Failed,
                var v when Verdict.Matches(v, Verdict.RequestChanges) => PhaseInput.RequestChanges,
                _ => PhaseInput.Succeeded, // PASS, APPROVE, or no verdict
            };

        var phaseDurationSeconds = pipeline.CurrentPhaseEntry?.StartedAt.HasValue == true
            ? (DateTime.UtcNow - pipeline.CurrentPhaseEntry.StartedAt.Value).TotalSeconds
            : 0;
        _logger.LogInformation(
            "Phase {Phase} for goal {GoalId} completed in {DurationSeconds:F1}s (model={Model})",
            pipeline.Phase, pipeline.GoalId, phaseDurationSeconds,
            string.IsNullOrEmpty(result.Model) ? "unknown" : result.Model);

        _logger.LogInformation("Verdict for {GoalId} phase {Phase}: {Verdict} → {PhaseInput}",
            pipeline.GoalId, pipeline.Phase, verdict, phaseInput);

        // PhaseLog: update the current entry with completion data
        if (pipeline.CurrentPhaseEntry is { } logEntry)
        {
            logEntry.CompletedAt = DateTime.UtcNow;
            logEntry.Verdict = verdict;
            logEntry.WorkerOutput = result.Output.Length > 4000
                ? result.Output[..4000] + $"... ({result.Output.Length} chars total)"
                : result.Output;
            logEntry.Result = phaseInput == PhaseInput.Failed ? PhaseOutcome.Fail : PhaseOutcome.Pass;
        }

        // State machine transition
        var transition = pipeline.StateMachine.Transition(phaseInput);

        switch (transition.Effect)
        {
            case TransitionEffect.Continue:
                pipeline.AdvanceTo(transition.NextPhase);
                var occurrenceIndex = pipeline.StateMachine.GetCurrentPhaseOccurrence(pipeline.Plan!.Phases);
                var nextPhaseInstructions = pipeline.Plan?.GetPhaseInstruction(transition.NextPhase, occurrenceIndex);
                await DispatchPhaseAsync(pipeline, transition.NextPhase, nextPhaseInstructions, ct, occurrenceIndex);
                break;

            case TransitionEffect.NewIteration:
                await HandleNewIterationAsync(pipeline, verdict, ct);
                break;

            case TransitionEffect.Completed:
                pipeline.AdvanceTo(GoalPhase.Done);
                await MarkGoalCompleted(pipeline, ct);
                break;
        }
    }

    /// <summary>
    /// Handles a <see cref="TransitionEffect.NewIteration"/> result from the state machine.
    /// Checks retry/iteration limits, re-plans, and dispatches the coder.
    /// </summary>
    private async Task HandleNewIterationAsync(
        GoalPipeline pipeline, string verdict, CancellationToken ct)
    {
        // Determine which retry counter to increment based on the verdict
        var isReviewRelated = Verdict.Matches(verdict, Verdict.RequestChanges)
            || pipeline.Metrics.ReviewVerdict == ReviewVerdict.RequestChanges;
        var canRetry = isReviewRelated
            ? pipeline.ReviewRetryBudget.TryConsume()
            : pipeline.TestRetryBudget.TryConsume();

        if (!canRetry)
        {
            pipeline.StateMachine.Fail();
            pipeline.AdvanceTo(GoalPhase.Failed);
            await MarkGoalFailed(pipeline,
                $"Exceeded max {(isReviewRelated ? "review" : "test")} retries", ct);
            return;
        }

        pipeline.AdvanceTo(GoalPhase.Coding);

        // Snapshot the ending iteration from PhaseLog
        var iterationSummary = BuildIterationSummary(pipeline);
        pipeline.CompletedIterationSummaries.Add(iterationSummary);

        // Persist the iteration summary to the goal source so the dashboard can read it
        var updateMeta = new GoalUpdateMetadata { IterationSummary = iterationSummary };
        await _goalManager.UpdateGoalStatusAsync(pipeline.GoalId, GoalStatus.InProgress, updateMeta, ct);

        if (!pipeline.IterationBudget.TryConsume())
        {
            pipeline.StateMachine.Fail();
            pipeline.AdvanceTo(GoalPhase.Failed);
            await MarkGoalFailed(pipeline, "Exceeded max iterations", ct);
            return;
        }

        // Capture review feedback before resetting metrics
        var reviewIssues = isReviewRelated && pipeline.Metrics.ReviewIssues is { Count: > 0 }
            ? pipeline.Metrics.ReviewIssues.ToList()
            : null;

        // Reset metrics for the new iteration
        pipeline.Metrics.ResetForNewIteration(pipeline.Iteration);

        // Re-plan the iteration with failure context
        IterationPlan newPlan;
        try
        {
            if (_brain is not null)
            {
                var rawPlan = await ResolvePlanAsync(pipeline, null, ct);
                var originalPhases = rawPlan.Phases.ToList();
                newPlan = ValidatePlan(rawPlan);

                if (!originalPhases.SequenceEqual(newPlan.Phases))
                {
                    var note = BuildPlanAdjustmentNote(originalPhases, newPlan.Phases);
                    await _brain.InjectSystemNoteAsync(pipeline, note, ct);
                }
            }
            else
            {
                newPlan = IterationPlan.Default();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to re-plan iteration for {GoalId}, using default plan",
                pipeline.GoalId);
            newPlan = IterationPlan.Default();
        }

        pipeline.SetPlan(newPlan);
        pipeline.StateMachine.StartIteration(newPlan.Phases);

        _logger.LogInformation(
            "New iteration {Iteration} for goal {GoalId}: {Phases}",
            pipeline.Iteration, pipeline.GoalId, string.Join(" → ", newPlan.Phases));

        // Build context for the coder
        var feedbackKind = isReviewRelated ? "Reviewer feedback" : "Test failures";
        var context = $"{feedbackKind}: see previous output.";

        if (reviewIssues is { Count: > 0 })
        {
            var allIssues = string.Join("\n", reviewIssues);
            context += $"\n\nAccumulated issues from all review rounds (fix ALL of these):\n{allIssues}";
        }

        var fixPrompt = _brain is not null
            ? await ResolvePromptAsync(pipeline, GoalPhase.Coding, context, ct)
            : $"Fix issues for: {pipeline.Description}. {context}";

        // PhaseLog: append a new entry for the coder phase in the new iteration
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding,
            Result = PhaseOutcome.Pass,
            Occurrence = 1,
            Iteration = pipeline.Iteration,
            StartedAt = DateTime.UtcNow,
        });
        if (pipeline.CurrentPhaseEntry is { } newIterEntry)
        {
            newIterEntry.WorkerPrompt = fixPrompt;
            newIterEntry.BrainPrompt = GetLastCraftPromptFromConversation(pipeline);
            // Capture planning prompt/response from conversation onto the first entry of the new iteration
            var (planningPrompt, planningResponse) = GetPlanningPromptsFromConversation(pipeline);
            newIterEntry.PlanningPrompt = planningPrompt;
            newIterEntry.PlanningResponse = planningResponse;
        }

        await DispatchToRole(pipeline, WorkerRole.Coder, fixPrompt, ct);
    }

    /// <summary>Dispatch a specific pipeline phase to the appropriate worker.</summary>
    private async Task DispatchPhaseAsync(
        GoalPipeline pipeline, GoalPhase phase, string? phaseInstructions, CancellationToken ct, int occurrence = 1)
    {
        // PhaseLog: append a new entry when the phase starts
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = phase,
            Result = PhaseOutcome.Pass,
            Occurrence = occurrence,
            Iteration = pipeline.Iteration,
            StartedAt = DateTime.UtcNow,
        });

        switch (phase)
        {
            case GoalPhase.Coding:
            case GoalPhase.Review:
            case GoalPhase.Testing:
            case GoalPhase.DocWriting:
                var prompt = _brain is not null
                    ? await ResolvePromptAsync(pipeline, phase, null, ct)
                    : $"Work on: {pipeline.Description} (phase: {phase})";
                if (pipeline.CurrentPhaseEntry is { } promptEntry)
                {
                    promptEntry.WorkerPrompt = prompt;
                    promptEntry.BrainPrompt = GetLastCraftPromptFromConversation(pipeline);
                }
                await DispatchToRole(pipeline, phase.ToWorkerRole(), prompt, ct);
                break;

            case GoalPhase.Improve:
                _logger.LogInformation("Dispatching Improver for goal {GoalId}", pipeline.GoalId);

                try
                {
                    await DispatchImproverCoreAsync(pipeline, phaseInstructions, ct);
                }
                catch (Exception ex)
                {
                    // Improve is non-blocking: if it fails, log and advance via state machine.
                    var skipReason = $"Improver failed: {ex.Message}";
                    _logger.LogWarning(ex, "Improver failed for goal {GoalId} — skipping to next phase. Reason: {Reason}",
                        pipeline.GoalId, skipReason);

                    // PhaseLog: mark the improver entry as skipped
                    if (pipeline.CurrentPhaseEntry is { } skipEntry && skipEntry.Name == GoalPhase.Improve)
                    {
                        skipEntry.CompletedAt = DateTime.UtcNow;
                        skipEntry.Result = PhaseOutcome.Skip;
                        skipEntry.Verdict = skipReason;
                    }

                    var notesMeta = new GoalUpdateMetadata
                    {
                        Notes = [$"Improver skipped: {ex.Message}"],
                    };
                    await _goalManager.UpdateGoalStatusAsync(pipeline.GoalId, GoalStatus.InProgress, notesMeta, ct);
                    await CommitGoalsToConfigRepoAsync(
                        $"Goal '{pipeline.GoalId}': improver skipped ({ex.GetType().Name})", ct);

                    // Advance past the failed Improve phase (non-blocking in state machine)
                    var skipResult = pipeline.StateMachine.Transition(PhaseInput.Failed);
                    pipeline.AdvanceTo(skipResult.NextPhase);
                    if (skipResult.Effect == TransitionEffect.Continue)
                        await DispatchPhaseAsync(pipeline, skipResult.NextPhase, null, ct);
                    else if (skipResult.Effect == TransitionEffect.Completed)
                        await MarkGoalCompleted(pipeline, ct);
                }
                break;

            case GoalPhase.Merging:
                await PerformMergeAsync(pipeline, ct);
                break;

            default:
                throw new InvalidOperationException(
                    $"Unexpected phase {phase} in plan for goal {pipeline.GoalId}");
        }
    }

    /// <summary>Core improver dispatch logic, extracted so the caller can catch failures gracefully.</summary>
    private async Task DispatchImproverCoreAsync(
        GoalPipeline pipeline, string? phaseInstructions, CancellationToken ct)
    {
        // Pull the config repo to ensure the improver container starts with the latest agents.md files
        await SyncAgentsFromConfigRepoAsync(ct);

        var analysis = "";
        if (_improvementAnalyzer is not null && _agentsManager is not null && _metricsTracker is not null)
            analysis = _improvementAnalyzer.BuildAnalysis(pipeline.Metrics, _metricsTracker.History);

        var improveContext = "Analyze the iteration and update the *.agents.md files directly.\n\n" + analysis + "\n\n"
            + "You have access to the agents/ folder containing *.agents.md files. "
            + "Read, edit, and save the files directly using the file tools. "
            + "Only modify files that need changes based on the evidence. "
            + "Do NOT modify any source code or tests — only *.agents.md files.";
        if (!string.IsNullOrEmpty(phaseInstructions))
            improveContext = phaseInstructions + "\n\n" + improveContext;

        var telemetryAggregator = new TelemetryAggregator();
        var telemetryRoleNames = WorkerRoles.TelemetryRoles.Select(r => r.ToRoleName());
        var stateDir = Environment.GetEnvironmentVariable("STATE_DIR") ?? "/app/state";
        var telemetrySummary = telemetryAggregator.AggregateTelemetry(stateDir, telemetryRoleNames);
        var telemetryText = telemetryAggregator.FormatSummary(telemetrySummary);
        if (!string.IsNullOrEmpty(telemetryText))
            improveContext += "\n\n## Telemetry\n" + telemetryText;
        telemetryAggregator.ClearTelemetryFiles(stateDir, telemetryRoleNames);

        var improvePrompt = _brain is not null
            ? await ResolvePromptAsync(pipeline, GoalPhase.Improve, improveContext, ct)
            : "Update the *.agents.md files based on iteration results.\n\n" + analysis;
        if (pipeline.CurrentPhaseEntry is { } improveEntry)
        {
            improveEntry.WorkerPrompt = improvePrompt;
            improveEntry.BrainPrompt = GetLastCraftPromptFromConversation(pipeline);
        }
        await DispatchToRole(pipeline, WorkerRole.Improver, improvePrompt, ct);
    }

    /// <summary>
    /// Validates and normalises an IterationPlan to enforce multi-round coding safety invariants:
    /// - Each Coding must be immediately followed by Testing (auto-insert if missing)
    /// - Exactly one Review is required after all Coding+Testing pairs (auto-insert if missing)
    /// - DocWriting and Improve rules unchanged (zero or one of each, same position rules)
    /// - Must end with Merging (auto-append if missing)
    ///
    /// For example, the Brain proposes ["coding", "coding", "review"] → output ["coding", "testing", "coding", "testing", "review", "merging"].
    /// </summary>
    /// <param name="plan">The plan to validate (phases list is modified in place).</param>
    /// <returns>The same plan object with validated phases.</returns>
    internal static IterationPlan ValidatePlan(IterationPlan plan)
    {
        var phases = plan.Phases;

        // Rule 1: Must contain Coding OR DocWriting (docs-only plans are valid)
        if (!phases.Contains(GoalPhase.Coding) && !phases.Contains(GoalPhase.DocWriting))
        {
            phases.Insert(0, GoalPhase.Coding);
        }

        if (phases.Contains(GoalPhase.Coding))
        {
            // Rule 2: Each Coding must be immediately followed by Testing.
            // Iterate backward so insertions don't shift indices we're about to process.
            for (var i = phases.Count - 1; i >= 0; i--)
            {
                if (phases[i] == GoalPhase.Coding && (i + 1 >= phases.Count || phases[i + 1] != GoalPhase.Testing))
                {
                    phases.Insert(i + 1, GoalPhase.Testing);
                }
            }

            // Rule 3: Exactly one Review, after all Coding+Testing pairs.
            // Remove any existing Review entries, then insert one after the last Testing.
            phases.RemoveAll(p => p == GoalPhase.Review);
            var lastTestingIndex = phases.LastIndexOf(GoalPhase.Testing);
            if (lastTestingIndex >= 0)
            {
                phases.Insert(lastTestingIndex + 1, GoalPhase.Review);
            }
            else
            {
                // No Testing found (shouldn't happen after Rule 2) — insert after last Coding
                var lastCodingIndex = phases.LastIndexOf(GoalPhase.Coding);
                phases.Insert(lastCodingIndex >= 0 ? lastCodingIndex + 1 : 0, GoalPhase.Review);
            }
        }
        else
        {
            // Docs-only plans: insert Testing only when neither Testing nor Review is present.
            if (!phases.Contains(GoalPhase.Testing) && !phases.Contains(GoalPhase.Review))
            {
                var docWritingIndex = phases.IndexOf(GoalPhase.DocWriting);
                var insertAt = docWritingIndex >= 0 ? docWritingIndex + 1 : phases.Count;
                phases.Insert(insertAt, GoalPhase.Testing);
            }
        }

        // Rule 4: Must end with Merging — remove any misplaced entries, then append
        phases.RemoveAll(p => p == GoalPhase.Merging);
        phases.Add(GoalPhase.Merging);

        return plan;
    }

    /// <summary>
    /// Builds a system note describing how the Brain's iteration plan was modified by
    /// <see cref="ValidatePlan"/> to satisfy safety requirements.
    /// Generates accurate per-change reasons for each adjustment made.
    /// </summary>
    /// <param name="original">The phases from the Brain's original plan.</param>
    /// <param name="final">The phases after validation was applied.</param>
    /// <returns>A human-readable note describing what was adjusted and why.</returns>
    internal static string BuildPlanAdjustmentNote(List<GoalPhase> original, List<GoalPhase> final)
    {
        var originalSet = new HashSet<GoalPhase>(original);
        var adjustments = new List<string>();

        // Coding was added as safety fallback (neither Coding nor DocWriting was present)
        if (!originalSet.Contains(GoalPhase.Coding) && !originalSet.Contains(GoalPhase.DocWriting)
            && final.Contains(GoalPhase.Coding))
        {
            adjustments.Add("- Coding was inserted at the start (required: every plan must contain Coding or DocWriting)");
        }

        // Testing was added — reference the actual preceding phase
        if (!originalSet.Contains(GoalPhase.Testing) && final.Contains(GoalPhase.Testing))
        {
            if (final.Contains(GoalPhase.Coding))
            {
                adjustments.Add("- Testing was inserted after Coding (required for code-change plans)");
            }
            else
            {
                // Docs-only plan: Testing inserted after DocWriting
                adjustments.Add("- Testing was inserted after DocWriting (required: docs-only plan had neither Testing nor Review)");
            }
        }

        // Review was added to a code-change plan
        if (!originalSet.Contains(GoalPhase.Review) && final.Contains(GoalPhase.Review))
        {
            adjustments.Add("- Review was inserted after Testing (required for code-change plans)");
        }

        // Merging adjustments: appended (absent) or moved to the end (misplaced)
        if (!originalSet.Contains(GoalPhase.Merging) && final.Contains(GoalPhase.Merging))
        {
            adjustments.Add("- Merging was appended as the final phase (always required)");
        }
        else
        {
            var originalMergingIndex = original.IndexOf(GoalPhase.Merging);
            var finalMergingIndex = final.IndexOf(GoalPhase.Merging);
            var mergingWasMoved = originalSet.Contains(GoalPhase.Merging)
                && originalMergingIndex != original.Count - 1
                && finalMergingIndex == final.Count - 1;
            if (mergingWasMoved)
            {
                adjustments.Add("- Merging was moved to the end (always required as the last phase)");
            }
        }

        var adjustmentsText = adjustments.Count > 0
            ? string.Join("\n", adjustments)
            : "- (phases were reordered to satisfy safety invariants)";

        return $"""
Your iteration plan was adjusted by the system to meet safety requirements.
Original plan: [{string.Join(", ", original)}]
Final plan: [{string.Join(", ", final)}]
Adjustments:
{adjustmentsText}
You will be asked to craft prompts for ALL phases in the final plan, including any that were added.
""";
    }

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
            await MarkGoalFailed(pipeline, ex.Message, ct);
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

    private async Task SendAgentsMdToWorkerAsync(ConnectedWorker worker, WorkerRole role, CancellationToken ct)
    {
        if (_agentsManager is null) return;
        var content = _agentsManager.GetAgentsMd(role);
        if (string.IsNullOrEmpty(content)) return;

        var roleName = role.ToRoleName();
        try
        {
            await _workerGateway.SendAgentsUpdateAsync(worker.Id, roleName, content, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send AGENTS.md to worker {WorkerId} for role {Role}",
                worker.Id, roleName);
        }
    }

    private async Task PerformMergeAsync(GoalPipeline pipeline, CancellationToken ct)
    {
        if (pipeline.CoderBranch is null)
        {
            await MarkGoalFailed(pipeline, "No coder branch set", ct);
            return;
        }

        _logger.LogInformation("Merging branch {Branch} for goal {GoalId}", pipeline.CoderBranch, pipeline.GoalId);

        try
        {
            var repos = ResolveRepositories(pipeline.Goal);
            var commitMessage = await GenerateMergeCommitMessageAsync(pipeline, ct);
            foreach (var repo in repos)
            {
                // Use the persistent brain clone — no temp dirs needed.
                // After merge, the clone is already on the base branch with the latest code.
                var mergeCommitHash = await _repoManager.MergeFeatureBranchAsync(
                    repo.Name, pipeline.CoderBranch, repo.DefaultBranch, commitMessage, ct);
                pipeline.MergeCommitHash = pipeline.MergeCommitHash is null
                    ? mergeCommitHash
                    : $"{pipeline.MergeCommitHash},{mergeCommitHash}";

                _logger.LogInformation("Squash-merged {Branch} into {Base} for {Repo} (commit={Hash})",
                    pipeline.CoderBranch, repo.DefaultBranch, repo.Name, mergeCommitHash);
            }

            // Summarize and merge goal session into master
            if (_brain is not null)
            {
                try
                {
                    await _brain.SummarizeAndMergeAsync(pipeline, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to summarize goal '{GoalId}' — deleting goal session without merge", pipeline.GoalId);
                    _brain.DeleteGoalSession(pipeline.GoalId);
                }
            }

            await MarkGoalCompleted(pipeline, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Merge failed for goal {GoalId} — checking if retryable", pipeline.GoalId);

            // State machine: merge failed → NewIteration (back to Coding)
            var mergeResult = pipeline.StateMachine.Transition(PhaseInput.Failed);
            if (mergeResult.Effect == TransitionEffect.NewIteration)
                await HandleMergeFailureAsync(pipeline, ex.Message, ct);
            else
                await MarkGoalFailed(pipeline, $"Unexpected merge failure effect: {mergeResult.Effect}", ct);
        }
    }

    /// <summary>
    /// When a merge fails (typically due to conflicts), send the goal back to the coder
    /// to rebase the feature branch onto the latest base branch. The full pipeline
    /// (Coding → Testing → Review → Merging) then runs again via the state machine.
    /// </summary>
    private async Task HandleMergeFailureAsync(GoalPipeline pipeline, string errorMessage, CancellationToken ct)
    {
        // State machine already transitioned to Coding (NewIteration) before this is called.
        // Check retry/iteration limits.
        if (!pipeline.ReviewRetryBudget.TryConsume())
        {
            pipeline.StateMachine.Fail();
            pipeline.AdvanceTo(GoalPhase.Failed);
            await MarkGoalFailed(pipeline, $"Merge failed after max retries: {errorMessage}", ct);
            return;
        }

        _logger.LogInformation(
            "Merge conflict for goal {GoalId} — sending back to Coder for rebase (retry {Retry}/{Max})",
            pipeline.GoalId, pipeline.ReviewRetryBudget.Used, pipeline.ReviewRetryBudget.Allowed);

        if (!pipeline.IterationBudget.TryConsume())
        {
            pipeline.StateMachine.Fail();
            pipeline.AdvanceTo(GoalPhase.Failed);
            await MarkGoalFailed(pipeline, "Exceeded max iterations during merge conflict resolution", ct);
            return;
        }
        pipeline.AdvanceTo(GoalPhase.Coding);

        var repos = ResolveRepositories(pipeline.Goal);
        var defaultBranch = repos.FirstOrDefault()?.DefaultBranch ?? "main";

        var rebaseContext = $"""
            Merge conflict: the feature branch could not be merged into {defaultBranch}.
            Error: {errorMessage}

            Your task: rebase the feature branch onto the latest {defaultBranch} and resolve all conflicts.
            The goal of the original changes was: {pipeline.Description}

            Steps:
            1. Run `git fetch origin`
            2. Run `git rebase origin/{defaultBranch}`
            3. Resolve any merge conflicts — keep the intent of the original changes
            4. Build and test to verify everything works
            5. Commit the resolved changes
            """;

        // Re-plan with full pipeline so the rebase goes through review and testing
        IterationPlan newPlan;
        try
        {
            if (_brain is not null)
            {
                var rawPlan = await ResolvePlanAsync(pipeline, null, ct);
                var originalPhases = rawPlan.Phases.ToList();
                newPlan = ValidatePlan(rawPlan);

                if (!originalPhases.SequenceEqual(newPlan.Phases))
                {
                    var note = BuildPlanAdjustmentNote(originalPhases, newPlan.Phases);
                    await _brain.InjectSystemNoteAsync(pipeline, note, ct);
                }
            }
            else
            {
                newPlan = IterationPlan.Default();
            }
        }
        catch
        {
            newPlan = IterationPlan.Default();
        }

        pipeline.SetPlan(newPlan);
        pipeline.StateMachine.StartIteration(newPlan.Phases);

        var fixPrompt = _brain is not null
            ? await ResolvePromptAsync(pipeline, GoalPhase.Coding, rebaseContext, ct)
            : rebaseContext;
        await DispatchToRole(pipeline, WorkerRole.Coder, fixPrompt, ct);
    }

    /// <summary>
    /// Sends an UpdateAgents message to all connected workers whose role matches the given role string.
    /// Best-effort: failures are logged but do not block the pipeline.
    /// </summary>
    private async Task BroadcastAgentsUpdateAsync(WorkerRole role, string content, CancellationToken ct)
    {
        var workers = _workerGateway.GetAllWorkers()
            .Where(w => w.Role == role);

        var roleName = role.ToRoleName();

        foreach (var worker in workers)
        {
            try
            {
                await _workerGateway.SendAgentsUpdateAsync(worker.Id, roleName, content, ct);
                _logger.LogInformation("Sent updated AGENTS.md to worker {WorkerId} (role={Role})", worker.Id, roleName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send AGENTS.md update to worker {WorkerId}", worker.Id);
            }
        }
    }

    /// <summary>
    /// Pulls the latest config repo and broadcasts any AGENTS.md changes to connected workers.
    /// Best-effort: failures are logged but do not block the main dispatch loop.
    /// </summary>
    private async Task SyncAgentsFromConfigRepoAsync(CancellationToken ct)
    {
        if (_configRepo is null || _agentsManager is null) return;

        try
        {
            await _configRepo.SyncRepoAsync(ct);

            foreach (var role in WorkerRoles.AgentRoles)
            {
                var roleName = role.ToRoleName();
                var repoContent = await _configRepo.LoadAgentsMdAsync(role, ct);
                if (string.IsNullOrEmpty(repoContent)) continue;

                var currentContent = _agentsManager.GetAgentsMd(role);
                if (repoContent == currentContent) continue;

                _agentsManager.UpdateAgentsMd(role, repoContent);

                // Broadcast to Docker workers via gRPC
                if (WorkerRoles.BroadcastableRoles.Contains(role))
                {
                    await BroadcastAgentsUpdateAsync(role, repoContent, ct);
                }

                // Inject updated orchestrator instructions into the Brain session
                if (role == WorkerRole.Orchestrator && _brain is not null)
                {
                    await _brain.InjectOrchestratorInstructionsAsync(repoContent, ct);
                }

                _logger.LogInformation("Synced {Role} AGENTS.md from config repo (changed)", roleName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync agents from config repo");
        }

        _lastAgentsSync = DateTime.UtcNow;
    }

    private async Task MarkGoalCompleted(GoalPipeline pipeline, CancellationToken ct)
    {
        // Defensive: prevent double-completion
        if (pipeline.Phase == GoalPhase.Done)
        {
            _logger.LogWarning("Goal {GoalId} is already Done — skipping duplicate completion", pipeline.GoalId);
            return;
        }

        pipeline.AdvanceTo(GoalPhase.Done);
        await FinalizeGoalAsync(pipeline, GoalStatus.Completed, failureReason: null,
            mergeCommitHash: pipeline.MergeCommitHash, ct);

        // Check for regression after recording metrics
        if (_metricsTracker is not null && _agentsManager is not null)
        {
            if (pipeline.Metrics.TotalTests == 0)
                _logger.LogWarning("Test metrics not extracted (TotalTests=0); regression check will skip test comparison.");

            if (_metricsTracker.HasRegressed(pipeline.Metrics))
            {
                _logger.LogWarning("⚠️ REGRESSION DETECTED for goal {GoalId} — rolling back AGENTS.md", pipeline.GoalId);

                // Only rollback roles whose AGENTS.md version changed this iteration
                var modifiedRoles = GetModifiedRoles(pipeline.Metrics);
                if (modifiedRoles.Count == 0)
                {
                    _logger.LogInformation("No AGENTS.md files were modified this iteration — nothing to rollback");
                }

                foreach (var role in modifiedRoles)
                {
                    try
                    {
                        _agentsManager.RollbackAgentsMd(role);
                        _logger.LogInformation("Rolled back {Role} AGENTS.md", role);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to rollback {Role} AGENTS.md", role);
                    }
                }
            }
            else
            {
                var comparison = _metricsTracker.CompareWithPrevious(pipeline.Metrics);
                if (comparison is not null)
                {
                    _logger.LogInformation(
                        "Metrics comparison for {GoalId}: CoverageDelta={CovDelta:+0.0;-0.0}%, PassRateDelta={PRDelta:+0.00;-0.00}",
                        pipeline.GoalId, comparison.CoverageDelta, comparison.PassRateDelta);
                }
            }
        }

        await TryAutoTagReleaseAsync(pipeline, ct);
    }

    /// <summary>
    /// Attempts to auto-assign a completed goal to a Planning release when exactly one exists.
    /// Only runs when the goal has no <see cref="Goal.ReleaseId"/> set and has at least one repository.
    /// Best-effort: failures are logged but do not fail the goal.
    /// </summary>
    private async Task TryAutoTagReleaseAsync(GoalPipeline pipeline, CancellationToken ct)
    {
        try
        {
            var store = _goalManager.Sources.OfType<IGoalStore>().FirstOrDefault();
            if (store is null)
                return;

            var goalId = pipeline.GoalId;
            var goal = await store.GetGoalAsync(goalId, ct);
            if (goal is null || goal.ReleaseId is not null || goal.RepositoryNames.Count == 0)
                return;

            var releases = await store.GetReleasesAsync(ct);
            var planningReleases = releases.Where(r => r.Status == ReleaseStatus.Planning).ToList();

            if (planningReleases.Count != 1)
                return;

            var planningRelease = planningReleases[0];
            goal = await store.GetGoalAsync(goalId, ct);
            if (goal is null || goal.ReleaseId is not null) return; // already tagged or deleted
            goal.ReleaseId = planningRelease.Id;
            await store.UpdateGoalAsync(goal, ct);

            _logger.LogInformation(
                "Auto-tagged goal {GoalId} to release {ReleaseId}",
                pipeline.GoalId, goal.ReleaseId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-tag goal {GoalId} to a release", pipeline.GoalId);
        }
    }

    private async Task MarkGoalFailed(GoalPipeline pipeline, string reason, CancellationToken ct)
    {
        pipeline.AdvanceTo(GoalPhase.Failed);
        await FinalizeGoalAsync(pipeline, GoalStatus.Failed, failureReason: reason,
            mergeCommitHash: null, ct);
    }

    private async Task FinalizeGoalAsync(
        GoalPipeline pipeline,
        GoalStatus status,
        string? failureReason,
        string? mergeCommitHash,
        CancellationToken ct)
    {
        var iterationSummary = BuildIterationSummary(pipeline);
        pipeline.CompletedIterationSummaries.Add(iterationSummary);

        var goalStartedAt = pipeline.GoalStartedAt ?? pipeline.Goal.StartedAt ?? pipeline.CreatedAt;
        var duration = pipeline.CompletedAt.HasValue
            ? pipeline.CompletedAt.Value - goalStartedAt
            : TimeSpan.Zero;

        var meta = new GoalUpdateMetadata
        {
            CompletedAt = pipeline.CompletedAt ?? DateTime.UtcNow,
            Iterations = pipeline.Iteration,
            IterationSummary = iterationSummary,
            TotalDurationSeconds = duration.TotalSeconds,
            FailureReason = failureReason,
            MergeCommitHash = mergeCommitHash,
        };

        await _goalManager.UpdateGoalStatusAsync(pipeline.GoalId, status, meta, ct);
        await CommitGoalsToConfigRepoAsync(
            failureReason is not null
                ? $"Goal '{pipeline.GoalId}' failed: {failureReason}"
                : $"Goal '{pipeline.GoalId}' completed",
            ct);

        pipeline.Metrics.Iteration = pipeline.Iteration;
        pipeline.Metrics.Duration = duration;
        pipeline.Metrics.Verdict = status == GoalStatus.Completed ? TaskVerdict.Pass : TaskVerdict.Fail;
        pipeline.Metrics.RetryCount = pipeline.ReviewRetries + pipeline.TestRetries;
        pipeline.Metrics.ReviewRetryCount = pipeline.ReviewRetries;
        pipeline.Metrics.TestRetryCount = pipeline.TestRetries;
        PopulateAgentsMdVersions(pipeline);
        _metricsTracker?.RecordIteration(pipeline.Metrics);
        await CommitMetricsToConfigRepoAsync(pipeline, ct);

        // Deregister from Brain — goal is no longer active
        (_brain as DistributedBrain)?.DeregisterActivePipeline(pipeline.GoalId);

        // Delete goal session on failure (no summary to merge)
        if (status == GoalStatus.Failed && _brain is not null)
            _brain.DeleteGoalSession(pipeline.GoalId);

        if (status == GoalStatus.Completed)
        {
            _logger.LogInformation("Goal {GoalId} completed in {Elapsed}", pipeline.GoalId, DurationFormatter.FormatDuration(duration));
            _logger.LogInformation(
                "🎉 Goal {GoalId} completed! Iterations={Iterations}, Duration={Duration:F1}min, " +
                "Tests={Passed}/{Total}, Coverage={Coverage:F1}%",
                pipeline.GoalId, pipeline.Iteration, duration.TotalMinutes,
                pipeline.Metrics.PassedTests, pipeline.Metrics.TotalTests,
                pipeline.Metrics.CoveragePercent);
        }
        else
            _logger.LogWarning("Goal {GoalId} failed: {Reason}", pipeline.GoalId, failureReason);
    }


    /// <summary>
    /// Builds a concise summary of worker output for the pipeline conversation.
    /// The Brain uses this to understand what each phase produced — especially
    /// WHY a reviewer rejected or what tests failed.
    /// </summary>
    internal static string BuildWorkerOutputSummary(GoalPhase phase, string verdict, TaskResult result)
    {
        var parts = new List<string> { $"Phase {phase} completed — verdict: {verdict}" };

        if (result.GitStatus is { } git && git.FilesChanged > 0)
            parts.Add($"Files changed: {git.FilesChanged} (+{git.Insertions} -{git.Deletions})");

        if (result.GitStatus is { FilesChanged: > 0, Pushed: false })
            parts.Add("⚠️ Git push FAILED — changes were not pushed to the remote");

        if (result.Metrics is { } m)
        {
            if (m.TotalTests > 0)
                parts.Add($"Tests: {m.PassedTests}/{m.TotalTests} passed, {m.FailedTests} failed");

            if (m.Issues is { Count: > 0 })
            {
                parts.Add("Issues found:");
                foreach (var issue in m.Issues)
                    parts.Add($"  - {issue}");
            }
        }

        // Include summary from structured metrics when available; fall back to truncated raw output
        var summary = result.Metrics?.Summary;
        if (!string.IsNullOrWhiteSpace(summary))
            parts.Add($"Worker summary:\n{summary}");
        else if (!string.IsNullOrWhiteSpace(result.Output))
        {
            const int maxOutputChars = 1500;
            var truncated = result.Output.Length > maxOutputChars
                ? result.Output[..maxOutputChars] + "..."
                : result.Output;
            parts.Add($"Worker output (no summary):\n{truncated}");
        }

        return string.Join("\n", parts);
    }

    /// <summary>
    /// Builds an <see cref="IterationSummary"/> from the pipeline's <see cref="GoalPipeline.PhaseLog"/>.
    /// Entries for the current iteration are extracted directly — no plan-walking or PhaseDurations needed.
    /// </summary>
    /// <param name="pipeline">Pipeline whose PhaseLog to summarise.</param>
    /// <returns>A populated <see cref="IterationSummary"/>.</returns>
    internal static IterationSummary BuildIterationSummary(GoalPipeline pipeline)
    {
        var metrics = pipeline.Metrics;
        var iteration = pipeline.Iteration;

        // Filter PhaseLog entries for this iteration
        var entries = pipeline.PhaseLog
            .Where(e => e.Iteration == iteration)
            .ToList();

        // The PhaseLog entries ARE the phases — same type, no mapping needed.
        var phases = entries.ToList();

        TestCounts? testCounts = metrics.TotalTests > 0
            ? new TestCounts
            {
                Total = metrics.TotalTests,
                Passed = metrics.PassedTests,
                Failed = metrics.FailedTests,
            }
            : null;

        string? reviewVerdict = metrics.ReviewVerdict switch
        {
            ReviewVerdict.Approve => "approve",
            ReviewVerdict.RequestChanges => "reject",
            _ => null,
        };

        // Check for improver skip in the PhaseLog
        var improverSkipEntry = entries.FirstOrDefault(e => e.Name == GoalPhase.Improve && e.Result == PhaseOutcome.Skip);
        var notes = new List<string>();
        if (improverSkipEntry is not null)
            notes.Add($"improver skipped: {improverSkipEntry.Verdict ?? "unknown"}");

        // Build PhaseOutputs dictionary from log entries for backward compat
        var phaseOutputs = new Dictionary<string, string>();
        foreach (var entry in entries.Where(e => e.WorkerOutput is not null))
        {
            var roleName = entry.Name.ToRoleName();
            if (!string.IsNullOrEmpty(roleName))
            {
                phaseOutputs[$"{roleName}-{entry.Iteration}"] = entry.WorkerOutput!;
                if (entry.Occurrence is > 0)
                    phaseOutputs[$"{roleName}-{entry.Iteration}-{entry.Occurrence}"] = entry.WorkerOutput!;
            }
        }

        return new IterationSummary
        {
            Iteration = iteration,
            Phases = phases,
            TestCounts = testCounts,
            BuildSuccess = metrics.BuildSuccess,
            ReviewVerdict = reviewVerdict,
            Notes = notes,
            PhaseOutputs = phaseOutputs,
            PlanReason = pipeline.Plan?.Reason,
            Clarifications = pipeline.Clarifications
                .Where(c => c.Iteration == iteration)
                .Select(c => new PersistedClarification
                {
                    Timestamp = c.Timestamp,
                    Phase = c.Phase,
                    WorkerRole = c.WorkerRole,
                    Question = c.Question,
                    Answer = c.Answer,
                    AnsweredBy = c.AnsweredBy,
                    Occurrence = c.Occurrence,
                })
                .ToList(),
        };
    }

    /// <summary>
    /// Commits and pushes the updated goals.yaml back to the config repo so external
    /// observers can see goal progress.
    /// </summary>
    private async Task CommitGoalsToConfigRepoAsync(string commitMessage, CancellationToken ct)
    {
        if (_configRepo is null)
            return;

        var goalsPath = Path.Combine(_configRepo.LocalPath, "goals.yaml");
        if (!File.Exists(goalsPath))
            return;

        try
        {
            await _configRepo.CommitFileAsync(goalsPath, commitMessage, ct);
            _logger.LogInformation("Committed goals.yaml update: {Message}", commitMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to commit goals.yaml update to config repo");
        }
    }

    /// <summary>
    /// Persists iteration metrics to the config repo as a JSON file under metrics/{goalId}.json.
    /// This creates a durable, version-controlled metrics history that survives container restarts
    /// and feeds the self-improvement loop.
    /// </summary>
    private async Task CommitMetricsToConfigRepoAsync(GoalPipeline pipeline, CancellationToken ct)
    {
        if (_configRepo is null)
            return;

        try
        {
            var metricsDir = Path.Combine(_configRepo.LocalPath, "metrics");
            Directory.CreateDirectory(metricsDir);

            var metricsPath = Path.Combine(metricsDir, $"{pipeline.GoalId}.json");
            var json = System.Text.Json.JsonSerializer.Serialize(pipeline.Metrics, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            });
            await File.WriteAllTextAsync(metricsPath, json, ct);

            await _configRepo.CommitFileAsync(metricsPath,
                $"Metrics for goal '{pipeline.GoalId}' ({pipeline.Metrics.Verdict})", ct);
            _logger.LogInformation("Committed metrics for {GoalId} to config repo", pipeline.GoalId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to commit metrics for {GoalId} to config repo", pipeline.GoalId);
        }
    }

    private void PopulateAgentsMdVersions(GoalPipeline pipeline)
    {
        if (_agentsManager is not null)
        {
            foreach (var role in WorkerRoles.AgentRoles)
            {
                var roleName = role.ToRoleName();
                var history = _agentsManager.GetHistory(role);
                pipeline.Metrics.AgentsMdVersions[roleName] = $"v{history.Length:D3}";
            }
        }
    }

    /// <summary>
    /// Determines which roles had their AGENTS.md modified this iteration by comparing
    /// current versions against the previous iteration's recorded versions.
    /// </summary>
    private List<WorkerRole> GetModifiedRoles(IterationMetrics current)
    {
        var modified = new List<WorkerRole>();
        var history = _metricsTracker!.History;

        // Need at least 2 entries: previous + current (just recorded)
        if (history.Count < 2)
            return modified;

        var previous = history[^2];

        foreach (var (roleName, currentVersion) in current.AgentsMdVersions)
        {
            if (!previous.AgentsMdVersions.TryGetValue(roleName, out var previousVersion)
                || currentVersion != previousVersion)
            {
                var role = WorkerRoleExtensions.ParseRole(roleName);
                if (role.HasValue)
                    modified.Add(role.Value);
            }
        }

        return modified;
    }

    /// <summary>
    /// Restore active pipelines from the persistence store on startup.
    /// Re-primes Brain sessions and marks goals as dispatched.
    /// </summary>
    private async Task RestoreActivePipelinesAsync(CancellationToken ct)
    {
        var restored = _pipelineManager.RestoreFromStore();
        if (restored.Count == 0)
        {
            // Even when there are no active pipelines to restore, clean up any orphaned
            // session files that may have been left by a previous crash.
            await CleanupOrphanedGoalSessionsAsync(ct);
            return;
        }

        _logger.LogInformation("Restoring {Count} active pipeline(s) from persistence store", restored.Count);

        foreach (var pipeline in restored)
        {
            _dispatchedGoals.TryAdd(pipeline.GoalId, true);

            // Register restored active pipelines with the Brain so get_goal tool works
            if (pipeline.Phase is not (GoalPhase.Done or GoalPhase.Failed))
                (_brain as DistributedBrain)?.RegisterActivePipeline(pipeline);

            // Ensure the goal session exists on disk. If the orchestrator restarted mid-dispatch,
            // the session may not have been forked yet. Fork from master to recover.
            if (_brain is not null && pipeline.Phase is not (GoalPhase.Done or GoalPhase.Failed))
            {
                if (!_brain.GoalSessionExists(pipeline.GoalId))
                {
                    await _brain.ForkSessionForGoalAsync(pipeline.GoalId, ct);
                }
            }

            if (pipeline.Phase is GoalPhase.Done or GoalPhase.Failed)
                continue;

            // If the pipeline was mid-planning (no ActiveTaskId and at Planning/Merging phase),
            // discard it and reset the goal so DispatchNextGoalAsync picks it up fresh.
            if (pipeline.ActiveTaskId is null && pipeline.Phase is GoalPhase.Planning or GoalPhase.Merging)
            {
                _logger.LogInformation("Pipeline {GoalId} was mid-{Phase} — discarding stale pipeline for fresh dispatch",
                    pipeline.GoalId, pipeline.Phase);

                _pipelineManager.RemovePipeline(pipeline.GoalId);
                _dispatchedGoals.TryRemove(pipeline.GoalId, out _);
                // Pipeline was registered above — clean it up from Brain's _activePipelines
                (_brain as DistributedBrain)?.DeregisterActivePipeline(pipeline.GoalId);
                await _goalManager.UpdateGoalStatusAsync(pipeline.GoalId, GoalStatus.Pending, null, ct);
                continue;
            }

            // If the pipeline was mid-task (has ActiveTaskId), the old worker is gone
            // after a restart. Clear the active task and enqueue for re-dispatch.
            if (pipeline.ActiveTaskId is not null)
            {
                _logger.LogInformation("Pipeline {GoalId} was mid-task ({TaskId}) — clearing stale task for re-dispatch",
                    pipeline.GoalId, pipeline.ActiveTaskId);

                var staleTaskId = pipeline.ActiveTaskId;
                _taskQueue.MarkComplete(staleTaskId);
                pipeline.ClearActiveTask();
                _redispatchQueue.Enqueue(pipeline.GoalId);
            }
        }

        _logger.LogInformation("Restored {Count} pipeline(s): {GoalIds}",
            restored.Count, string.Join(", ", restored.Select(p => p.GoalId)));

        // Clean up any orphaned goal session files whose goals are no longer active
        await CleanupOrphanedGoalSessionsAsync(ct);
    }

    /// <summary>
    /// Scans for orphaned brain-goal-*.json files and deletes any whose goalId is not
    /// in the active pipeline set. Called at the end of <see cref="RestoreActivePipelinesAsync"/>
    /// to remove stale session files left behind by crashed or interrupted runs.
    /// </summary>
    private async Task CleanupOrphanedGoalSessionsAsync(CancellationToken ct)
    {
        if (_brain is not DistributedBrain db)
            return;

        var stateDir = db.StateDirectory;
        if (string.IsNullOrEmpty(stateDir) || !Directory.Exists(stateDir))
            return;

        var activeGoalIds = _pipelineManager.GetActivePipelines()
            .Select(p => p.GoalId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.GetFiles(stateDir, "brain-goal-*.json"))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var goalId = fileName.Replace("brain-goal-", "");
            if (!activeGoalIds.Contains(goalId))
            {
                File.Delete(file);
                _logger.LogInformation("Cleaned up orphaned goal session: {GoalId}", goalId);
            }
        }

        await Task.CompletedTask;
    }

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
        await CommitGoalsToConfigRepoAsync($"Goal '{goal.Id}' started", ct);

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

    internal static string InjectTokenIntoUrl(string url)
    {
        var token = Environment.GetEnvironmentVariable("GH_TOKEN");
        if (string.IsNullOrEmpty(token) || !url.StartsWith("https://github.com/"))
            return url;

        return url.Replace("https://github.com/", $"https://x-access-token:{token}@github.com/");
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

    /// <summary>
    /// Builds a squash-merge commit message from a goal ID and description.
    /// The first line is formatted as <c>Goal: {goalId} — {summary}</c> and truncated to 120 characters.
    /// When the description exceeds the truncation limit, the full description is appended as the commit body.
    /// </summary>
    /// <param name="goalId">The goal identifier.</param>
    /// <param name="description">The goal description (may be multi-line or very long).</param>
    /// <returns>A commit message suitable for a squash merge commit.</returns>
    internal static string BuildSquashCommitMessage(string goalId, string description)
    {
        const int MaxSummaryLength = 120;

        // Use only the first line of the description as the summary
        var firstLine = description.Split('\n', StringSplitOptions.None)[0].Trim();
        var subject = $"Goal: {goalId} \u2014 {firstLine}";

        if (subject.Length <= MaxSummaryLength && firstLine == description.Trim())
        {
            // Short single-line description — subject only
            return subject;
        }

        // Truncate the subject line if needed
        if (subject.Length > MaxSummaryLength)
            subject = subject[..MaxSummaryLength];

        // Append the full description as the commit body
        return $"{subject}\n\n{description.Trim()}";
    }

    /// <summary>
    /// Extracts the last "craft-prompt" user entry from the pipeline conversation
    /// for the current iteration. This is the prompt that was sent TO the Brain
    /// when asking it to craft a worker prompt.
    /// </summary>
    internal static string? GetLastCraftPromptFromConversation(GoalPipeline pipeline)
    {
        return pipeline.Conversation
            .LastOrDefault(e => e.Iteration == pipeline.Iteration
                             && e.Purpose == "craft-prompt"
                             && e.Role == "user")
            ?.Content;
    }

    /// <summary>
    /// Extracts the planning prompt and response from the pipeline conversation
    /// for the current iteration. Returns the last "planning" user/assistant pair.
    /// </summary>
    internal static (string? Prompt, string? Response) GetPlanningPromptsFromConversation(GoalPipeline pipeline)
    {
        var planningEntries = pipeline.Conversation
            .Where(e => e.Iteration == pipeline.Iteration && e.Purpose == "planning")
            .ToList();
        var prompt = planningEntries.LastOrDefault(e => e.Role == "user")?.Content;
        var response = planningEntries.LastOrDefault(e => e.Role == "assistant")?.Content;
        return (prompt, response);
    }
}
