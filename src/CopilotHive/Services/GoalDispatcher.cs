using System.Collections.Concurrent;
using CopilotHive.Agents;
using CopilotHive.Configuration;
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
    private readonly BrainRepoManager _repoManager;
    private readonly ILogger<GoalDispatcher> _logger;
    private readonly HiveConfigFile? _config;

    private readonly BranchCoordinator _branchCoordinator = new();
    private readonly TaskBuilder _taskBuilder = new(new BranchCoordinator());
    private readonly ConcurrentDictionary<string, bool> _dispatchedGoals = new();
    private readonly ConcurrentQueue<string> _redispatchQueue = new();
    private DateTime _lastAgentsSync = DateTime.MinValue;
    private readonly TimeSpan _startupDelay;

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
    /// <param name="startupDelay">Delay before the first dispatch poll; defaults to 10 seconds to give workers time to connect.</param>
    public GoalDispatcher(
        GoalManager goalManager,
        GoalPipelineManager pipelineManager,
        TaskQueue taskQueue,
        IWorkerGateway workerGateway,
        TaskCompletionNotifier completionNotifier,
        ILogger<GoalDispatcher> logger,
        BrainRepoManager repoManager,
        IDistributedBrain? brain = null,
        HiveConfigFile? config = null,
        MetricsTracker? metricsTracker = null,
        AgentsManager? agentsManager = null,
        ImprovementAnalyzer? improvementAnalyzer = null,
        ConfigRepoManager? configRepo = null,
        TimeSpan? startupDelay = null)
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
        _startupDelay = startupDelay ?? TimeSpan.FromSeconds(10);

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

    /// <inheritdoc/>
    /// <summary>
    /// Handles a question from a worker tool call by routing it to the Brain.
    /// Returns the Brain's response as a string.
    /// </summary>
    public async Task<string> AskBrainAsync(GoalPipeline pipeline, string question, CancellationToken ct)
    {
        if (_brain is null)
            return "Brain is not available. Please proceed with your best judgment.";

        _logger.LogInformation("Worker asks Brain: {Question}", question);
        var answer = await _brain.CraftPromptAsync(pipeline, pipeline.Phase, $"A worker asks: {question}", ct);
        _logger.LogInformation("Brain answers: {Answer}", answer[..Math.Min(answer.Length, 200)]);
        return answer;
    }

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
        // No-op detection: if the coder returned without making any file changes,
        // skip verdict extraction and immediately retry with a stronger prompt.
        if (pipeline.Phase == GoalPhase.Coding && (result.GitStatus?.FilesChanged ?? 0) == 0)
        {
            _logger.LogWarning(
                "No-op detected: Coder for {GoalId} returned with 0 files changed — retrying with stronger prompt",
                pipeline.GoalId);

            if (!pipeline.IncrementIteration())
            {
                await MarkGoalFailed(pipeline, "Coder produced no file changes after max iterations (no-op)", ct);
                return;
            }

            var noOpContext =
                "CRITICAL: Your previous attempt produced ZERO file changes. " +
                "You MUST edit files and commit them with `git add -A && git commit`. " +
                "Do NOT just describe or discuss changes — actually make them.\n\n" +
                $"Previous coder output (for context):\n{(result.Output.Length > 500 ? result.Output[..500] + "..." : result.Output)}";

            var retryPrompt = _brain is not null
                ? await _brain.CraftPromptAsync(pipeline, GoalPhase.Coding, noOpContext, ct)
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

        // Extract structured verdict from worker tool call metrics
        var verdict = "PASS"; // Default: worker completed successfully
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

            if (pipeline.Phase == GoalPhase.Review && verdict is "APPROVE" or "REQUEST_CHANGES")
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
        pipeline.Conversation.Add(new ConversationEntry(workerRole, outputSummary));

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
                "FAIL" or "CANCELLED" => PhaseInput.Failed,
                "REQUEST_CHANGES" => PhaseInput.RequestChanges,
                _ => PhaseInput.Succeeded, // PASS, APPROVE, or no verdict
            };

        var phaseDurationSeconds = pipeline.PhaseStartedAt.HasValue
            ? (DateTime.UtcNow - pipeline.PhaseStartedAt.Value).TotalSeconds
            : 0;
        _logger.LogInformation(
            "Phase {Phase} for goal {GoalId} completed in {DurationSeconds:F1}s (model={Model})",
            pipeline.Phase, pipeline.GoalId, phaseDurationSeconds,
            string.IsNullOrEmpty(result.Model) ? "unknown" : result.Model);

        _logger.LogInformation("Verdict for {GoalId} phase {Phase}: {Verdict} → {PhaseInput}",
            pipeline.GoalId, pipeline.Phase, verdict, phaseInput);

        // State machine transition
        var transition = pipeline.StateMachine.Transition(phaseInput);

        switch (transition.Effect)
        {
            case TransitionEffect.Continue:
                pipeline.AdvanceTo(transition.NextPhase);
                string? instructions = null;
                pipeline.Plan?.PhaseInstructions.TryGetValue(transition.NextPhase, out instructions);
                await DispatchPhaseAsync(pipeline, transition.NextPhase, instructions, ct);
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
        var isReviewRelated = verdict == "REQUEST_CHANGES"
            || pipeline.Metrics.ReviewVerdict == ReviewVerdict.RequestChanges;
        var canRetry = isReviewRelated
            ? pipeline.IncrementReviewRetry()
            : pipeline.IncrementTestRetry();

        if (!canRetry)
        {
            pipeline.StateMachine.Fail();
            pipeline.AdvanceTo(GoalPhase.Failed);
            await MarkGoalFailed(pipeline,
                $"Exceeded max {(isReviewRelated ? "review" : "test")} retries", ct);
            return;
        }

        // Advance to Coding — this records the duration of the just-ended phase
        // (Review or Testing) into Metrics.PhaseDurations via AdvanceTo.
        var failedPhase = isReviewRelated ? GoalPhase.Review : GoalPhase.Testing;
        pipeline.AdvanceTo(GoalPhase.Coding);

        // Snapshot the ending iteration (PhaseDurations now includes the failed phase)
        var iterationSummary = BuildIterationSummary(pipeline, failedPhase);
        pipeline.CompletedIterationSummaries.Add(iterationSummary);

        // Persist the iteration summary to the goal source so the dashboard can read it
        var updateMeta = new GoalUpdateMetadata { IterationSummary = iterationSummary };
        await _goalManager.UpdateGoalStatusAsync(pipeline.GoalId, GoalStatus.InProgress, updateMeta, ct);

        if (!pipeline.IncrementIteration())
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
            newPlan = _brain is not null
                ? ValidatePlan(await _brain.PlanIterationAsync(pipeline, ct))
                : IterationPlan.Default();
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
            ? await _brain.CraftPromptAsync(pipeline, GoalPhase.Coding, context, ct)
            : $"Fix issues for: {pipeline.Description}. {context}";

        await DispatchToRole(pipeline, WorkerRole.Coder, fixPrompt, ct);
    }

    /// <summary>Dispatch a specific pipeline phase to the appropriate worker.</summary>
    private async Task DispatchPhaseAsync(
        GoalPipeline pipeline, GoalPhase phase, string? phaseInstructions, CancellationToken ct)
    {
        switch (phase)
        {
            case GoalPhase.Coding:
            case GoalPhase.Review:
            case GoalPhase.Testing:
                var prompt = _brain is not null
                    ? await _brain.CraftPromptAsync(pipeline, phase, phaseInstructions, ct)
                    : $"Work on: {pipeline.Description} (phase: {phase})";
                await DispatchToRole(pipeline, phase.ToWorkerRole(), prompt, ct);
                break;

            case GoalPhase.DocWriting:
                var docPrompt = BuildDocWriterPrompt(pipeline, phaseInstructions);
                await DispatchToRole(pipeline, WorkerRole.DocWriter, docPrompt, ct);
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

                    pipeline.Metrics.ImproverSkipped = true;
                    pipeline.Metrics.ImproverSkipReason = skipReason;

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
            ? await _brain.CraftPromptAsync(pipeline, GoalPhase.Improve, improveContext, ct)
            : "Update the *.agents.md files based on iteration results.\n\n" + analysis;
        await DispatchToRole(pipeline, WorkerRole.Improver, improvePrompt, ct);
    }

    /// <summary>
    /// Validates an IterationPlan to ensure safety invariants:
    /// must contain Coding, at least one of Testing or Review, and end with Merging.
    /// Missing phases are inserted in the correct position.
    /// </summary>
    internal static IterationPlan ValidatePlan(IterationPlan plan)
    {
        var phases = plan.Phases;

        // Must contain Coding
        if (!phases.Contains(GoalPhase.Coding))
        {
            phases.Insert(0, GoalPhase.Coding);
        }

        // Must contain at least one of Testing or Review
        if (!phases.Contains(GoalPhase.Testing) && !phases.Contains(GoalPhase.Review))
        {
            var codingIndex = phases.IndexOf(GoalPhase.Coding);
            phases.Insert(codingIndex + 1, GoalPhase.Testing);
        }

        // Must end with Merging — remove any misplaced Merging entries, then ensure it's last
        phases.RemoveAll(p => p == GoalPhase.Merging);
        phases.Add(GoalPhase.Merging);

        return plan;
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

        var task = _taskBuilder.Build(
            goalId: pipeline.GoalId,
            goalDescription: pipeline.Description,
            role: role,
            iteration: pipeline.Iteration,
            repositories: repositories,
            prompt: prompt,
            branchAction: branchAction,
            model: model);

        // Improver operates read-only: it can see the feature branch but must not push.
        // Downgrade the action to Unspecified so the worker runtime skips push operations.
        if (role == WorkerRole.Improver && task.BranchInfo is not null)
        {
            task.BranchInfo.Action = BranchAction.Unspecified;
        }

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
        if (!pipeline.IncrementReviewRetry())
        {
            pipeline.StateMachine.Fail();
            pipeline.AdvanceTo(GoalPhase.Failed);
            await MarkGoalFailed(pipeline, $"Merge failed after max retries: {errorMessage}", ct);
            return;
        }

        _logger.LogInformation(
            "Merge conflict for goal {GoalId} — sending back to Coder for rebase (retry {Retry}/{Max})",
            pipeline.GoalId, pipeline.ReviewRetries, pipeline.MaxRetries);

        if (!pipeline.IncrementIteration())
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
            newPlan = _brain is not null
                ? ValidatePlan(await _brain.PlanIterationAsync(pipeline, ct))
                : IterationPlan.Default();
        }
        catch
        {
            newPlan = IterationPlan.Default();
        }

        pipeline.SetPlan(newPlan);
        pipeline.StateMachine.StartIteration(newPlan.Phases);

        var fixPrompt = _brain is not null
            ? await _brain.CraftPromptAsync(pipeline, GoalPhase.Coding, rebaseContext, ct)
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

        var goalStartedAt = pipeline.GoalStartedAt ?? pipeline.Goal.StartedAt ?? pipeline.CreatedAt;
        var duration = pipeline.CompletedAt.HasValue
            ? pipeline.CompletedAt.Value - goalStartedAt
            : TimeSpan.Zero;

        var completedMeta = new GoalUpdateMetadata
        {
            CompletedAt = pipeline.CompletedAt ?? DateTime.UtcNow,
            Iterations = pipeline.Iteration,
            PhaseDurations = pipeline.Metrics.PhaseDurations is { Count: > 0 }
                ? pipeline.Metrics.PhaseDurations.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.TotalSeconds)
                : null,
            IterationSummary = BuildIterationSummary(pipeline, failedPhase: null),
            TotalDurationSeconds = duration.TotalSeconds,
            MergeCommitHash = pipeline.MergeCommitHash,
        };
        await _goalManager.UpdateGoalStatusAsync(pipeline.GoalId, GoalStatus.Completed, completedMeta, ct);
        await CommitGoalsToConfigRepoAsync($"Goal '{pipeline.GoalId}' completed", ct);

        pipeline.Metrics.Iteration = pipeline.Iteration;
        pipeline.Metrics.Duration = duration;
        pipeline.Metrics.Verdict = TaskVerdict.Pass;
        pipeline.Metrics.RetryCount = pipeline.ReviewRetries + pipeline.TestRetries;
        pipeline.Metrics.ReviewRetryCount = pipeline.ReviewRetries;
        pipeline.Metrics.TestRetryCount = pipeline.TestRetries;
        PopulateAgentsMdVersions(pipeline);
        _metricsTracker?.RecordIteration(pipeline.Metrics);
        await CommitMetricsToConfigRepoAsync(pipeline, ct);

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

        _logger.LogInformation("Goal {GoalId} completed in {Elapsed}", pipeline.GoalId, DurationFormatter.FormatDuration(duration));
        _logger.LogInformation(
            "🎉 Goal {GoalId} completed! Iterations={Iterations}, Duration={Duration:F1}min, " +
            "Tests={Passed}/{Total}, Coverage={Coverage:F1}%",
            pipeline.GoalId, pipeline.Iteration, duration.TotalMinutes,
            pipeline.Metrics.PassedTests, pipeline.Metrics.TotalTests,
            pipeline.Metrics.CoveragePercent);
    }

    private async Task MarkGoalFailed(GoalPipeline pipeline, string reason, CancellationToken ct)
    {
        var failedPhase = pipeline.Phase; // capture before AdvanceTo overwrites it
        pipeline.AdvanceTo(GoalPhase.Failed);

        var failedMeta = new GoalUpdateMetadata
        {
            CompletedAt = pipeline.CompletedAt ?? DateTime.UtcNow,
            Iterations = pipeline.Iteration,
            FailureReason = reason,
            PhaseDurations = pipeline.Metrics.PhaseDurations is { Count: > 0 }
                ? pipeline.Metrics.PhaseDurations.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.TotalSeconds)
                : null,
            IterationSummary = BuildIterationSummary(pipeline, failedPhase),
        };
        await _goalManager.UpdateGoalStatusAsync(pipeline.GoalId, GoalStatus.Failed, failedMeta, ct);
        await CommitGoalsToConfigRepoAsync($"Goal '{pipeline.GoalId}' failed: {reason}", ct);

        var duration = pipeline.CompletedAt.HasValue
            ? pipeline.CompletedAt.Value - pipeline.CreatedAt
            : TimeSpan.Zero;

        pipeline.Metrics.Iteration = pipeline.Iteration;
        pipeline.Metrics.Duration = duration;
        pipeline.Metrics.Verdict = TaskVerdict.Fail;
        pipeline.Metrics.RetryCount = pipeline.ReviewRetries + pipeline.TestRetries;
        pipeline.Metrics.ReviewRetryCount = pipeline.ReviewRetries;
        pipeline.Metrics.TestRetryCount = pipeline.TestRetries;
        PopulateAgentsMdVersions(pipeline);
        _metricsTracker?.RecordIteration(pipeline.Metrics);
        await CommitMetricsToConfigRepoAsync(pipeline, ct);

        _logger.LogWarning("Goal {GoalId} failed: {Reason}", pipeline.GoalId, reason);
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

        // Include a truncated portion of the raw output for additional context
        // (e.g. reviewer's detailed explanation, test failure stack traces)
        var rawOutput = result.Output;
        if (!string.IsNullOrWhiteSpace(rawOutput))
        {
            const int maxOutputChars = 1500;
            var truncated = rawOutput.Length > maxOutputChars
                ? rawOutput[..maxOutputChars] + "..."
                : rawOutput;
            parts.Add($"Worker output:\n{truncated}");
        }

        return string.Join("\n", parts);
    }

    /// <summary>
    /// Builds an <see cref="IterationSummary"/> from the pipeline's current metrics.
    /// All phases tracked in <see cref="IterationMetrics.PhaseDurations"/> are included;
    /// the <paramref name="failedPhase"/> (if provided) is marked as "fail", all others as "pass".
    /// </summary>
    /// <param name="pipeline">Pipeline whose metrics to summarise.</param>
    /// <param name="failedPhase">The phase that caused failure, or <c>null</c> for a completed goal.</param>
    /// <returns>A populated <see cref="IterationSummary"/>.</returns>
    internal static IterationSummary BuildIterationSummary(GoalPipeline pipeline, GoalPhase? failedPhase)
    {
        var metrics = pipeline.Metrics;

        var phases = metrics.PhaseDurations
            .Select(kvp =>
            {
                // Determine role key from phase name to look up output
                var roleName = PhaseNameToRoleName(kvp.Key);
                pipeline.PhaseOutputs.TryGetValue($"{roleName}-{pipeline.Iteration}", out var output);

                return new PhaseResult
                {
                    Name = kvp.Key,
                    Result = failedPhase.HasValue && kvp.Key == failedPhase.Value.ToString() ? "fail" : "pass",
                    DurationSeconds = kvp.Value.TotalSeconds,
                    WorkerOutput = string.IsNullOrEmpty(output) ? null : output,
                };
            })
            .ToList();

        // Phases that were skipped are not in PhaseDurations — add them separately.
        // If PhaseDurations already recorded an "Improve" entry (e.g. the phase started then failed),
        // remove it so the summary never contains duplicate phase names.
        if (metrics.ImproverSkipped)
        {
            phases.RemoveAll(p => p.Name == "Improve");
            phases.Add(new PhaseResult { Name = "Improve", Result = "skip", DurationSeconds = 0 });
        }

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

        var notes = new List<string>();
        if (metrics.ImproverSkipped && !string.IsNullOrWhiteSpace(metrics.ImproverSkipReason))
            notes.Add($"improver skipped: {metrics.ImproverSkipReason}");

        // Build PhaseOutputs dictionary for the iteration summary (keyed by "{role}-{iteration}")
        var phaseOutputs = new Dictionary<string, string>();
        foreach (var (key, output) in pipeline.PhaseOutputs)
        {
            // Only include outputs for the current iteration (key format: "{role}-{iteration}")
            if (key.EndsWith($"-{pipeline.Iteration}"))
                phaseOutputs[key] = output;
        }

        return new IterationSummary
        {
            Iteration = pipeline.Iteration,
            Phases = phases,
            TestCounts = testCounts,
            ReviewVerdict = reviewVerdict,
            Notes = notes,
            PhaseOutputs = phaseOutputs,
        };
    }

    /// <summary>Maps a phase name string to its worker role name for output lookup.</summary>
    private static string PhaseNameToRoleName(string phaseName) => phaseName switch
    {
        "Coding" => "coder",
        "Testing" => "tester",
        "Review" => "reviewer",
        "DocWriting" => "docwriter",
        "Improve" => "improver",
        _ => "",
    };

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
            return;

        _logger.LogInformation("Restoring {Count} active pipeline(s) from persistence store", restored.Count);

        foreach (var pipeline in restored)
        {
            _dispatchedGoals.TryAdd(pipeline.GoalId, true);

            // Brain session is loaded from file at startup (single persistent session),
            // so no per-goal re-priming is needed.

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
                ? await _brain.CraftPromptAsync(pipeline, pipeline.Phase,
                    "This task is being re-dispatched after the previous worker was lost. Continue from where the previous worker left off.",
                    ct)
                : $"Continue task for: {pipeline.Description}";

            await DispatchToRole(pipeline, role.Value, prompt, ct);
        }
    }

    private async Task DispatchNextGoalAsync(CancellationToken ct)
    {
        // Sequential gate: only one goal runs at a time so the Brain
        // can accumulate context across goals in its single session.
        var activePipelines = _pipelineManager.GetActivePipelines();
        if (activePipelines.Count > 0)
            return;

        var goal = await _goalManager.GetNextGoalAsync(ct);
        if (goal is null)
            return;

        if (!_dispatchedGoals.TryAdd(goal.Id, true))
            return;

        _logger.LogInformation("Dispatching goal '{GoalId}': {Description} (Priority={Priority})", goal.Id, goal.Description, goal.Priority);

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

        // Plan iteration phases
        IterationPlan iterationPlan;
        if (_brain is not null)
        {
            try
            {
                iterationPlan = ValidatePlan(await _brain.PlanIterationAsync(pipeline, ct));
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
        pipeline.AdvanceTo(GoalPhase.Coding);

        // Craft prompt for coder and dispatch
        var coderPrompt = _brain is not null
            ? await _brain.CraftPromptAsync(pipeline, GoalPhase.Coding, null, ct)
            : BuildCoderPrompt(goal);

        await DispatchToRole(pipeline, WorkerRole.Coder, coderPrompt, ct);

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
    /// Builds a doc-writer prompt directly (bypasses Brain to avoid goal-description echo).
    /// </summary>
    private string BuildDocWriterPrompt(GoalPipeline pipeline, string? phaseInstructions)
    {
        var additionalContext = phaseInstructions is not null
            ? $"\nAdditional context from the Brain:\n{phaseInstructions}\n"
            : "";

        return $"""
            You are the doc-writer. Your ONLY job is to update documentation for the code changes
            that have already been made on this branch.

            Goal summary: {pipeline.Description}
            {additionalContext}
            Your tasks (do ALL of these):
            1. Run `git diff origin/<base-branch>...HEAD --stat` to see ALL files changed on this branch
            2. Run `git diff origin/<base-branch>...HEAD` to review the full diff
            3. Update the CHANGELOG.md — add entries under [Unreleased] describing what was added/changed/fixed
            4. Verify XML doc comments on new/changed public APIs are present and accurate — flag missing or incorrect ones in your report but do NOT edit source code files (that is the coder's job)
            5. Update README.md if the changes affect user-facing features or configuration
            6. Run `git add -A && git commit` with message "docs: update documentation for [brief description]"

            CRITICAL RULES:
            - Do NOT edit source code (.cs files) — that is the coder's job
            - Do NOT write or run tests — that is the tester's job
            - Do NOT run git push — the orchestrator handles that
            - Only edit documentation files: CHANGELOG.md, README.md, and other .md files

            When done, call the `report_doc_changes` tool with:
            - verdict: "PASS" if you successfully updated documentation, "FAIL" if you could not
            - filesUpdated: array of files you changed (e.g. ["CHANGELOG.md", "README.md"])
            - summary: brief description of what you documented
            """;
    }

    private static string InjectTokenIntoUrl(string url)
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
}
