using System.Collections.Concurrent;
using CopilotHive.Agents;
using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Improvement;
using CopilotHive.Metrics;
using CopilotHive.Orchestration;
using CopilotHive.Shared.Grpc;

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
    private readonly WorkerPool _workerPool;
    private readonly IDistributedBrain? _brain;
    private readonly ImprovementAnalyzer? _improvementAnalyzer;
    private readonly AgentsManager? _agentsManager;
    private readonly MetricsTracker? _metricsTracker;
    private readonly ConfigRepoManager? _configRepo;
    private readonly ILogger<GoalDispatcher> _logger;
    private readonly HiveConfigFile? _config;

    private readonly BranchCoordinator _branchCoordinator = new();
    private readonly TaskBuilder _taskBuilder = new(new BranchCoordinator());
    private readonly ConcurrentDictionary<string, bool> _dispatchedGoals = new();
    private DateTime _lastAgentsSync = DateTime.MinValue;

    /// <summary>
    /// Initialises a new <see cref="GoalDispatcher"/> with required and optional dependencies.
    /// </summary>
    /// <param name="goalManager">Source of pending goals.</param>
    /// <param name="pipelineManager">Tracks active goal pipelines.</param>
    /// <param name="taskQueue">Queue used to dispatch task assignments to workers.</param>
    /// <param name="workerPool">Registry of currently connected workers.</param>
    /// <param name="completionNotifier">Bridge that delivers task completion events to this dispatcher.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="brain">Optional LLM brain for intelligent prompt crafting.</param>
    /// <param name="config">Optional hive configuration from the config repo.</param>
    /// <param name="metricsTracker">Optional metrics tracker for the improvement cycle.</param>
    /// <param name="agentsManager">Optional manager for per-role AGENTS.md files.</param>
    /// <param name="improvementAnalyzer">Optional analyzer that decides when to run the improver.</param>
    /// <param name="configRepo">Optional config repo manager for syncing AGENTS.md files.</param>
    public GoalDispatcher(
        GoalManager goalManager,
        GoalPipelineManager pipelineManager,
        TaskQueue taskQueue,
        WorkerPool workerPool,
        TaskCompletionNotifier completionNotifier,
        ILogger<GoalDispatcher> logger,
        IDistributedBrain? brain = null,
        HiveConfigFile? config = null,
        MetricsTracker? metricsTracker = null,
        AgentsManager? agentsManager = null,
        ImprovementAnalyzer? improvementAnalyzer = null,
        ConfigRepoManager? configRepo = null)
    {
        _goalManager = goalManager;
        _pipelineManager = pipelineManager;
        _taskQueue = taskQueue;
        _workerPool = workerPool;
        _brain = brain;
        _improvementAnalyzer = improvementAnalyzer;
        _agentsManager = agentsManager;
        _metricsTracker = metricsTracker;
        _logger = logger;
        _config = config;
        _configRepo = configRepo;

        completionNotifier.OnTaskCompleted += complete => HandleTaskCompletionAsync(complete);
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
        var decision = await _brain.DecideNextStepAsync(pipeline, $"A worker asks: {question}", ct);
        var answer = decision.Prompt ?? decision.Action.ToString();
        _logger.LogInformation("Brain answers: {Answer}", answer[..Math.Min(answer.Length, 200)]);
        return answer;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("GoalDispatcher started — polling for goals every {Interval}s (Brain: {BrainEnabled})",
            PollInterval.TotalSeconds, _brain is not null ? "enabled" : "disabled");

        // Restore any in-flight pipelines from the persistence store
        await RestoreActivePipelinesAsync(stoppingToken);

        // Sync agents from config repo at startup
        await SyncAgentsFromConfigRepoAsync(stoppingToken);

        // Give workers time to connect before dispatching
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow - _lastAgentsSync > AgentsSyncInterval)
                {
                    await SyncAgentsFromConfigRepoAsync(stoppingToken);
                }

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
    public async Task HandleTaskCompletionAsync(TaskComplete complete, CancellationToken ct = default)
    {
        var pipeline = _pipelineManager.GetByTaskId(complete.TaskId);
        if (pipeline is null)
        {
            _logger.LogWarning("No pipeline found for completed task {TaskId}", complete.TaskId);
            return;
        }

        _logger.LogInformation("Pipeline {GoalId} task completed (phase={Phase}, status={Status})",
            pipeline.GoalId, pipeline.Phase, complete.Status);

        if (_brain is null)
        {
            await MarkGoalCompleted(pipeline, ct);
            return;
        }

        try
        {
            await DriveNextPhaseAsync(pipeline, complete, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error driving pipeline {GoalId} to next phase", pipeline.GoalId);
            await MarkGoalFailed(pipeline, ex.Message, ct);
        }

        _pipelineManager.PersistFull(pipeline);
    }

    private async Task DriveNextPhaseAsync(GoalPipeline pipeline, TaskComplete complete, CancellationToken ct)
    {
        // No-op detection: if the coder returned without making any file changes,
        // skip Brain interpretation and immediately retry with a stronger prompt.
        if (pipeline.Phase == GoalPhase.Coding && (complete.GitStatus?.FilesChanged ?? 0) == 0)
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
                "You MUST edit files and commit them with git. " +
                "Do NOT just describe or discuss changes — actually make them. " +
                "Verify your work with 'git status' and 'git log --oneline -3' before finishing.\n\n" +
                $"Previous coder output (for context):\n{(complete.Output.Length > 500 ? complete.Output[..500] + "..." : complete.Output)}";

            var retryPrompt = await _brain!.CraftPromptAsync(pipeline, "coder", noOpContext, ct);
            await DispatchToRole(pipeline, WorkerRole.Coder, retryPrompt, ct);
            return;
        }

        // Log the raw worker output BEFORE Brain interpretation — critical for debugging
        var outputPreview = complete.Output.Length > 2000
            ? complete.Output[..2000] + $"... ({complete.Output.Length} chars total)"
            : complete.Output;
        _logger.LogInformation(
            "Worker output for {GoalId} (phase={Phase}):\n{Output}",
            pipeline.GoalId, pipeline.Phase, outputPreview);

        // Ask the Brain to interpret the worker output
        var interpretation = await _brain!.InterpretOutputAsync(
            pipeline,
            pipeline.Phase.ToString().ToLowerInvariant(),
            complete.Output,
            ct);

        _logger.LogInformation("Brain interpretation for {GoalId}: Action={Action}, Verdict={Verdict}",
            pipeline.GoalId, interpretation.Action, interpretation.Verdict ?? interpretation.ReviewVerdict);

        // After Improver: sync config repo to pick up the changes it pushed directly
        if (pipeline.Phase == GoalPhase.Improve)
        {
            _logger.LogInformation("Improver completed for goal {GoalId} — syncing config repo for updated agents.md files",
                pipeline.GoalId);
            await SyncAgentsFromConfigRepoAsync(ct);
        }

        // Update metrics if available from Brain interpretation
        if (interpretation.TestMetrics is not null)
        {
            pipeline.Metrics.TotalTests = interpretation.TestMetrics.TotalTests ?? 0;
            pipeline.Metrics.PassedTests = interpretation.TestMetrics.PassedTests ?? 0;
            pipeline.Metrics.FailedTests = interpretation.TestMetrics.FailedTests ?? 0;
            pipeline.Metrics.CoveragePercent = interpretation.TestMetrics.CoveragePercent ?? 0;
            pipeline.Metrics.BuildSuccess = interpretation.TestMetrics.BuildSuccess ?? false;
        }

        // Fallback: if this is the Testing phase and Brain didn't extract metrics,
        // parse the raw worker output for test result patterns (e.g. "Passed: 268")
        if (pipeline.Phase == GoalPhase.Testing && pipeline.Metrics.TotalTests == 0)
        {
            FallbackParseTestMetrics(complete.Output, pipeline.Metrics);
            if (pipeline.Metrics.TotalTests > 0)
                _logger.LogInformation(
                    "Fallback metrics parsed for {GoalId}: {Passed}/{Total} passed, {Failed} failed",
                    pipeline.GoalId, pipeline.Metrics.PassedTests, pipeline.Metrics.TotalTests, pipeline.Metrics.FailedTests);
        }

        // Populate review metrics when the reviewer phase completes
        if (pipeline.Phase == GoalPhase.Review)
        {
            pipeline.Metrics.ReviewVerdict = interpretation.ReviewVerdict ?? "";
            if (interpretation.Issues is { Count: > 0 })
            {
                pipeline.Metrics.ReviewIssuesFound += interpretation.Issues.Count;
                pipeline.Metrics.ReviewIssues.AddRange(interpretation.Issues);
            }
        }

        if (interpretation.Issues is not null)
            pipeline.Metrics.Issues.AddRange(interpretation.Issues);

        // Decide next step based on the action
        switch (interpretation.Action)
        {
            case OrchestratorActionType.SpawnCoder:
                await DispatchToRole(pipeline, WorkerRole.Coder, interpretation.Prompt, ct);
                break;

            case OrchestratorActionType.SpawnReviewer:
                pipeline.AdvanceTo(GoalPhase.Review);
                await DispatchToRole(pipeline, WorkerRole.Reviewer, interpretation.Prompt, ct);
                break;

            case OrchestratorActionType.SpawnTester:
                pipeline.AdvanceTo(GoalPhase.Testing);
                await DispatchToRole(pipeline, WorkerRole.Tester, interpretation.Prompt, ct);
                break;

            case OrchestratorActionType.SpawnImprover:
                pipeline.AdvanceTo(GoalPhase.Improve);
                await DispatchToRole(pipeline, WorkerRole.Improver, interpretation.Prompt, ct);
                break;

            case OrchestratorActionType.RequestChanges:
                if (!pipeline.IncrementReviewRetry())
                {
                    await MarkGoalFailed(pipeline, "Exceeded max review retries", ct);
                    return;
                }
                if (!pipeline.IncrementIteration())
                {
                    await MarkGoalFailed(pipeline, "Exceeded max iterations", ct);
                    return;
                }
                pipeline.AdvanceTo(GoalPhase.Coding);

                // Build cumulative context: include ALL review issues found so far,
                // not just the latest round, so the coder addresses everything at once.
                var allIssues = BuildAccumulatedReviewIssues(pipeline);
                var fixContext = $"Latest reviewer feedback:\n{interpretation.Reason}"
                    + (allIssues.Length > 0
                        ? $"\n\nAccumulated issues from all review rounds (fix ALL of these):\n{allIssues}"
                        : "");

                var fixPrompt = await _brain.CraftPromptAsync(pipeline, "coder", fixContext, ct);
                await DispatchToRole(pipeline, WorkerRole.Coder, fixPrompt, ct);
                break;

            case OrchestratorActionType.Retry:
                if (!pipeline.IncrementTestRetry())
                {
                    await MarkGoalFailed(pipeline, "Exceeded max test retries", ct);
                    return;
                }
                if (!pipeline.IncrementIteration())
                {
                    await MarkGoalFailed(pipeline, "Exceeded max iterations", ct);
                    return;
                }
                pipeline.AdvanceTo(GoalPhase.Coding);
                var retryPrompt = await _brain.CraftPromptAsync(pipeline, "coder",
                    $"Test failures:\n{interpretation.Reason}", ct);
                await DispatchToRole(pipeline, WorkerRole.Coder, retryPrompt, ct);
                break;

            case OrchestratorActionType.Merge:
                // Enforce remaining phases before merge
                var mergeEnforced = await EnforceRemainingPhasesAsync(pipeline, ct,
                    interpretation.Verdict ?? interpretation.ReviewVerdict, interpretation.Reason);
                if (!mergeEnforced)
                {
                    pipeline.AdvanceTo(GoalPhase.Merging);
                    await PerformMergeAsync(pipeline, ct);
                }
                break;

            case OrchestratorActionType.Done:
            case OrchestratorActionType.Skip:
                // Enforce minimum pipeline phases before allowing completion
                var enforced = await EnforceRemainingPhasesAsync(pipeline, ct,
                    interpretation.Verdict ?? interpretation.ReviewVerdict, interpretation.Reason);
                if (!enforced)
                    await MarkGoalCompleted(pipeline, ct);
                break;

            default:
                _logger.LogWarning(
                    "Unrecognized Brain action '{Action}' for {GoalId} (phase={Phase}) — enforcing remaining phases",
                    interpretation.Action, pipeline.GoalId, pipeline.Phase);

                // Instead of recursing, enforce remaining pipeline phases or complete
                var defaultEnforced = await EnforceRemainingPhasesAsync(pipeline, ct,
                    interpretation.Verdict ?? interpretation.ReviewVerdict ?? "UNKNOWN",
                    interpretation.Reason ?? $"Unrecognized action: {interpretation.Action}");
                if (!defaultEnforced)
                    await MarkGoalCompleted(pipeline, ct);
                break;
        }
    }

    /// <summary>
    /// When the Brain says "Done" too early, enforce any remaining mandatory pipeline phases.
    /// Returns true if a phase was dispatched (i.e., don't mark complete yet).
    /// Phase order is determined by the pipeline's IterationPlan (from Brain or default fallback).
    /// If Review or Testing had a FAIL verdict, loop back to Coding instead of advancing.
    /// </summary>
    private async Task<bool> EnforceRemainingPhasesAsync(
        GoalPipeline pipeline, CancellationToken ct, string? verdict = null, string? reason = null)
    {
        // If Review or Testing failed, loop back to Coding for another attempt
        if (verdict is "FAIL" && pipeline.Phase is GoalPhase.Review or GoalPhase.Testing)
        {
            var isReview = pipeline.Phase == GoalPhase.Review;
            var canRetry = isReview ? pipeline.IncrementReviewRetry() : pipeline.IncrementTestRetry();
            if (!canRetry)
            {
                await MarkGoalFailed(pipeline,
                    $"Exceeded max {(isReview ? "review" : "test")} retries", ct);
                return true;
            }

            _logger.LogInformation(
                "{Phase} failed for goal {GoalId} — sending back to Coder (reason: {Reason})",
                pipeline.Phase, pipeline.GoalId, reason ?? "unspecified");

            if (!pipeline.IncrementIteration())
            {
                await MarkGoalFailed(pipeline, "Exceeded max iterations", ct);
                return true;
            }
            pipeline.AdvanceTo(GoalPhase.Coding);
            pipeline.ClearPlan();

            // Re-plan iteration with failure context so the Brain can adjust strategy
            if (_brain is not null)
            {
                try
                {
                    var newPlan = await _brain.PlanIterationAsync(pipeline, ct);
                    ValidatePlan(newPlan);
                    pipeline.SetPlan(newPlan);
                    _logger.LogInformation(
                        "Re-planned iteration {Iteration} for goal {GoalId} after {Phase} failure: {Reason}",
                        pipeline.Iteration, pipeline.GoalId, pipeline.Phase, newPlan.Reason);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to re-plan iteration for goal {GoalId}, using default plan",
                        pipeline.GoalId);
                    pipeline.SetPlan(IterationPlan.Default());
                }
            }
            else
            {
                pipeline.SetPlan(IterationPlan.Default());
            }

            var feedbackKind = isReview ? "Reviewer feedback" : "Test failures";
            var fixPrompt = _brain is not null
                ? await _brain.CraftPromptAsync(pipeline, "coder",
                    $"{feedbackKind}:\n{reason ?? "See previous output."}", ct)
                : $"Fix issues for: {pipeline.Description}. {feedbackKind}:\n{reason}";
            await DispatchToRole(pipeline, WorkerRole.Coder, fixPrompt, ct);
            return true;
        }

        // Ensure we have an iteration plan — create default fallback if missing
        if (pipeline.Plan is null)
        {
            var alwaysImprove = _config?.Orchestrator?.AlwaysImprove ?? false;
            var shouldImprove = alwaysImprove || (_improvementAnalyzer?.ShouldImprove(pipeline.Metrics) ?? false);
            pipeline.SetPlan(IterationPlan.Default(shouldImprove));
        }

        // Enforce always_improve: inject Improve phase before Merging if Brain omitted it
        var alwaysImproveConfig = _config?.Orchestrator?.AlwaysImprove ?? false;
        if (alwaysImproveConfig && !pipeline.Plan!.Phases.Contains(GoalPhase.Improve))
        {
            var mergingIndex = pipeline.Plan.Phases.IndexOf(GoalPhase.Merging);
            if (mergingIndex >= 0)
                pipeline.Plan.Phases.Insert(mergingIndex, GoalPhase.Improve);
            else
                pipeline.Plan.Phases.Add(GoalPhase.Improve);
        }

        var plan = pipeline.Plan!;

        // Sync plan position to the current pipeline phase
        while (plan.CurrentPhase is not null && plan.CurrentPhase != pipeline.Phase)
        {
            plan.Advance();
        }

        // Advance past the current phase to get the next one
        var nextPhase = plan.Advance();

        if (nextPhase is null)
            return false;

        _logger.LogInformation(
            "Enforcing pipeline phase {Phase} for goal {GoalId} (Brain wanted Done at {CurrentPhase})",
            nextPhase, pipeline.GoalId, pipeline.Phase);

        // Get phase-specific instructions from the plan (if any)
        plan.PhaseInstructions.TryGetValue(nextPhase.Value, out var phaseInstructions);

        await DispatchPhaseAsync(pipeline, nextPhase.Value, phaseInstructions, ct);
        return true;
    }

    /// <summary>Dispatch a specific pipeline phase to the appropriate worker.</summary>
    private async Task DispatchPhaseAsync(
        GoalPipeline pipeline, GoalPhase phase, string? phaseInstructions, CancellationToken ct)
    {
        switch (phase)
        {
            case GoalPhase.Review:
                pipeline.AdvanceTo(GoalPhase.Review);
                var reviewPrompt = _brain is not null
                    ? await _brain.CraftPromptAsync(pipeline, "reviewer", phaseInstructions, ct)
                    : $"Review the code changes for: {pipeline.Description}";
                await DispatchToRole(pipeline, WorkerRole.Reviewer, reviewPrompt, ct);
                break;

            case GoalPhase.Testing:
                pipeline.AdvanceTo(GoalPhase.Testing);
                var testPrompt = _brain is not null
                    ? await _brain.CraftPromptAsync(pipeline, "tester", phaseInstructions, ct)
                    : $"Run the existing tests and verify the changes for: {pipeline.Description}";
                await DispatchToRole(pipeline, WorkerRole.Tester, testPrompt, ct);
                break;

            case GoalPhase.Improve:
                pipeline.AdvanceTo(GoalPhase.Improve);
                _logger.LogInformation("Dispatching Improver for goal {GoalId}", pipeline.GoalId);

                // Pull the config repo to ensure the improver container starts with the latest agents.md files
                await SyncAgentsFromConfigRepoAsync(ct);

                var analysis = "";
                if (_improvementAnalyzer is not null && _agentsManager is not null && _metricsTracker is not null)
                {
                    var agentsMd = new Dictionary<string, string>();
                    foreach (var role in new[] { "coder", "tester", "reviewer", "improver" })
                    {
                        var content = _agentsManager.GetAgentsMd(role);
                        if (!string.IsNullOrEmpty(content)) agentsMd[role] = content;
                    }
                    analysis = _improvementAnalyzer.BuildAnalysis(pipeline.Metrics, _metricsTracker.History, agentsMd);
                }

                var improveContext = "Analyze the iteration and update the *.agents.md files directly.\n\n" + analysis + "\n\n"
                    + "You have access to the agents/ folder containing *.agents.md files. "
                    + "Read, edit, and save the files directly using the file tools. "
                    + "Only modify files that need changes based on the evidence. "
                    + "Do NOT modify any source code or tests — only *.agents.md files.";
                if (!string.IsNullOrEmpty(phaseInstructions))
                    improveContext = phaseInstructions + "\n\n" + improveContext;

                var improvePrompt = _brain is not null
                    ? await _brain.CraftPromptAsync(pipeline, "improver", improveContext, ct)
                    : "Update the *.agents.md files based on iteration results.\n\n" + analysis;
                await DispatchToRole(pipeline, WorkerRole.Improver, improvePrompt, ct);
                break;

            case GoalPhase.Merging:
                pipeline.AdvanceTo(GoalPhase.Merging);
                await PerformMergeAsync(pipeline, ct);
                break;

            default:
                _logger.LogWarning("Unexpected phase {Phase} in plan for goal {GoalId} — skipping",
                    phase, pipeline.GoalId);
                break;
        }
    }

    /// <summary>
    /// Validates an IterationPlan to ensure safety invariants:
    /// must contain Coding, at least one of Review or Testing, and end with Merging.
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

        // Must contain at least one of Review or Testing
        if (!phases.Contains(GoalPhase.Review) && !phases.Contains(GoalPhase.Testing))
        {
            var codingIndex = phases.IndexOf(GoalPhase.Coding);
            phases.Insert(codingIndex + 1, GoalPhase.Review);
        }

        // Must end with Merging — remove any misplaced Merging entries, then ensure it's last
        phases.RemoveAll(p => p == GoalPhase.Merging);
        phases.Add(GoalPhase.Merging);

        return plan;
    }

    private async Task DispatchToRole(GoalPipeline pipeline, WorkerRole role, string? prompt, CancellationToken ct)
    {
        // If no prompt provided (Brain might have returned null), craft one
        if (string.IsNullOrWhiteSpace(prompt) && _brain is not null)
        {
            prompt = await _brain.CraftPromptAsync(pipeline, role.ToString().ToLowerInvariant(), null, ct);
        }

        prompt ??= $"Work on: {pipeline.Description}";

        // Log the prompt being sent to the worker
        var promptPreview = prompt.Length > 1500
            ? prompt[..1500] + $"... ({prompt.Length} chars total)"
            : prompt;
        _logger.LogDebug("Prompt for {Role} (goal={GoalId}):\n{Prompt}",
            role, pipeline.GoalId, promptPreview);

        var branchAction = pipeline.CoderBranch is null ? BranchAction.Create : BranchAction.Checkout;
        var repositories = ResolveRepositories(pipeline.Goal);

        // Resolve per-role model from config
        var roleName = role switch
        {
            WorkerRole.Coder => "coder",
            WorkerRole.Reviewer => "reviewer",
            WorkerRole.Tester => "tester",
            WorkerRole.Improver => "improver",
            _ => "coder",
        };
        var model = _config?.GetModelForRole(roleName);

        var task = _taskBuilder.Build(
            goalId: pipeline.GoalId,
            goalDescription: pipeline.Description,
            role: role,
            iteration: pipeline.Iteration,
            repositories: repositories,
            prompt: prompt,
            branchAction: branchAction,
            model: model);

        // Non-coder roles reuse the coder's branch (all work on the same feature branch)
        if (branchAction == BranchAction.Checkout && pipeline.CoderBranch is not null && task.BranchInfo is not null)
        {
            task.BranchInfo.FeatureBranch = pipeline.CoderBranch;
        }

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
        var idleWorker = _workerPool.GetIdleWorker(role);
        if (idleWorker is not null)
        {
            var queuedTask = _taskQueue.TryDequeue(role);
            if (queuedTask is not null)
            {
                _taskQueue.Activate(queuedTask, idleWorker.Id);
                _workerPool.MarkBusy(idleWorker.Id, queuedTask.TaskId);
                await idleWorker.MessageChannel.Writer.WriteAsync(
                    new OrchestratorMessage { Assignment = queuedTask }, ct);
                _logger.LogInformation("Task {TaskId} pushed to worker {WorkerId}", queuedTask.TaskId, idleWorker.Id);
            }
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
            foreach (var repo in repos)
            {
                var tempDir = Path.Combine(Path.GetTempPath(), $"hive-merge-{Guid.NewGuid():N}");
                try
                {
                    await RunGitAsync($"clone --branch {repo.DefaultBranch} {repo.Url} {tempDir}", ct);
                    await RunGitAsync($"-C {tempDir} config user.email copilothive@local", ct);
                    await RunGitAsync($"-C {tempDir} config user.name CopilotHive", ct);
                    await RunGitAsync($"-C {tempDir} fetch origin {pipeline.CoderBranch}", ct);
                    await RunGitAsync($"-C {tempDir} merge origin/{pipeline.CoderBranch} --no-edit", ct);
                    await RunGitAsync($"-C {tempDir} push origin {repo.DefaultBranch}", ct);

                    _logger.LogInformation("Merged {Branch} into {Base} for {Repo}",
                        pipeline.CoderBranch, repo.DefaultBranch, repo.Name);
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        try { Directory.Delete(tempDir, true); }
                        catch { /* Best effort cleanup */ }
                    }
                }
            }

            await MarkGoalCompleted(pipeline, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Merge failed for goal {GoalId} — checking if retryable", pipeline.GoalId);
            await HandleMergeFailureAsync(pipeline, ex.Message, ct);
        }
    }

    /// <summary>
    /// When a merge fails (typically due to conflicts), send the goal back to the coder
    /// to rebase the feature branch onto the latest base branch. The full pipeline
    /// (Coding → Review → Testing → Merging) then runs again.
    /// </summary>
    private async Task HandleMergeFailureAsync(GoalPipeline pipeline, string errorMessage, CancellationToken ct)
    {
        // Use the review retry counter to limit total merge-conflict retries
        if (!pipeline.IncrementReviewRetry())
        {
            await MarkGoalFailed(pipeline, $"Merge failed after max retries: {errorMessage}", ct);
            return;
        }

        _logger.LogInformation(
            "Merge conflict for goal {GoalId} — sending back to Coder for rebase (retry {Retry}/{Max})",
            pipeline.GoalId, pipeline.ReviewRetries, pipeline.MaxRetries);

        if (!pipeline.IncrementIteration())
        {
            await MarkGoalFailed(pipeline, "Exceeded max iterations during merge conflict resolution", ct);
            return;
        }
        pipeline.AdvanceTo(GoalPhase.Coding);
        pipeline.ClearPlan();

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
            4. Run `dotnet build` and `dotnet test` to verify everything works
            5. Commit the resolved changes
            """;

        // Re-plan with full pipeline so the rebase goes through review and testing
        if (_brain is not null)
        {
            try
            {
                var newPlan = await _brain.PlanIterationAsync(pipeline, ct);
                ValidatePlan(newPlan);
                pipeline.SetPlan(newPlan);
            }
            catch
            {
                pipeline.SetPlan(IterationPlan.Default());
            }
        }
        else
        {
            pipeline.SetPlan(IterationPlan.Default());
        }

        var fixPrompt = _brain is not null
            ? await _brain.CraftPromptAsync(pipeline, "coder", rebaseContext, ct)
            : rebaseContext;
        await DispatchToRole(pipeline, WorkerRole.Coder, fixPrompt, ct);
    }

    private static async Task RunGitAsync(string arguments, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"git {arguments} failed: {stderr}");
        }
    }

    /// <summary>
    /// Sends an UpdateAgents message to all connected workers whose role matches the given role string.
    /// Best-effort: failures are logged but do not block the pipeline.
    /// </summary>
    private async Task BroadcastAgentsUpdateAsync(string role, string content, CancellationToken ct)
    {
        var workers = _workerPool.GetAllWorkers()
            .Where(w => w.Role.ToString().Equals(role, StringComparison.OrdinalIgnoreCase));

        var message = new OrchestratorMessage
        {
            UpdateAgents = new UpdateAgents
            {
                AgentsMdContent = content,
                Role = role,
            }
        };

        foreach (var worker in workers)
        {
            try
            {
                await worker.MessageChannel.Writer.WriteAsync(message, ct);
                _logger.LogInformation("Sent updated AGENTS.md to worker {WorkerId} (role={Role})", worker.Id, role);
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

            foreach (var role in new[] { "coder", "tester", "reviewer", "improver", "orchestrator" })
            {
                var repoContent = await _configRepo.LoadAgentsMdAsync(role, ct);
                if (string.IsNullOrEmpty(repoContent)) continue;

                var currentContent = _agentsManager.GetAgentsMd(role);
                if (repoContent == currentContent) continue;

                _agentsManager.UpdateAgentsMd(role, repoContent);
                await BroadcastAgentsUpdateAsync(role, repoContent, ct);
                _logger.LogInformation("Synced {Role} AGENTS.md from config repo (changed)", role);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync agents from config repo");
        }

        _lastAgentsSync = DateTime.UtcNow;
    }

    /// <summary>
    /// Collects all review issues from past Brain interpretations in the pipeline's conversation history.
    /// Returns a deduplicated summary the Brain can use to ensure the coder fixes everything.
    /// </summary>
    private static string BuildAccumulatedReviewIssues(GoalPipeline pipeline)
    {
        var allIssues = new List<string>();
        var round = 0;

        foreach (var entry in pipeline.Conversation)
        {
            if (entry.Role != "assistant")
                continue;

            // Look for review interpretation responses containing REQUEST_CHANGES
            var content = entry.Content;
            if (!content.Contains("REQUEST_CHANGES", StringComparison.OrdinalIgnoreCase))
                continue;

            round++;

            // Extract individual issue strings from the "issues" JSON array
            var issueStart = content.IndexOf("\"issues\"", StringComparison.Ordinal);
            if (issueStart < 0)
                continue;

            var arrayStart = content.IndexOf('[', issueStart);
            var arrayEnd = content.IndexOf(']', arrayStart > 0 ? arrayStart : issueStart);
            if (arrayStart < 0 || arrayEnd < 0)
                continue;

            var arrayContent = content[(arrayStart + 1)..arrayEnd];
            foreach (var part in arrayContent.Split('"'))
            {
                var trimmed = part.Trim().Trim(',').Trim();
                if (trimmed.Length > 10)
                    allIssues.Add($"[Round {round}] {trimmed}");
            }
        }

        return allIssues.Count > 0
            ? string.Join("\n", allIssues)
            : "";
    }

    /// <summary>
    /// Fallback metric extraction from raw worker output when the Brain doesn't return test_metrics.
    /// Parses common patterns including dotnet test summaries, markdown tables, and key-value lines.
    /// </summary>
    internal static void FallbackParseTestMetrics(string output, IterationMetrics metrics)
    {
        if (string.IsNullOrEmpty(output))
            return;

        // dotnet test summary: "Passed!  - Failed:     0, Passed:   268, Skipped:     0, Total:   268"
        var dotnetMatch = System.Text.RegularExpressions.Regex.Match(
            output,
            @"Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (dotnetMatch.Success)
        {
            metrics.FailedTests = int.Parse(dotnetMatch.Groups[1].Value);
            metrics.PassedTests = int.Parse(dotnetMatch.Groups[2].Value);
            metrics.TotalTests = int.Parse(dotnetMatch.Groups[4].Value);
            FallbackParseBuildSuccess(output, metrics);
            return;
        }

        // Markdown table format: "| Total | 273 |" or "| Passed | 273 |"
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();

            // Match markdown table rows like "| Total | 273 |"
            var mdMatch = System.Text.RegularExpressions.Regex.Match(
                trimmed, @"^\|\s*(\w+)\s*\|\s*(\d+)\s*\|");
            if (mdMatch.Success)
            {
                var key = mdMatch.Groups[1].Value;
                var val = int.Parse(mdMatch.Groups[2].Value);
                if (key.Equals("Total", StringComparison.OrdinalIgnoreCase))
                    metrics.TotalTests = val;
                else if (key.Equals("Passed", StringComparison.OrdinalIgnoreCase))
                    metrics.PassedTests = val;
                else if (key.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                    metrics.FailedTests = val;
                else if (key.Equals("Errors", StringComparison.OrdinalIgnoreCase))
                    metrics.BuildSuccess = val == 0;
                continue;
            }

            // Key-value line format: "total_tests: 273"
            if (trimmed.StartsWith("unit_tests_total:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("total_tests:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("total:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(trimmed[(trimmed.IndexOf(':') + 1)..].Trim(), out var t))
                    metrics.TotalTests = t;
            }
            else if (trimmed.StartsWith("unit_tests_passed:", StringComparison.OrdinalIgnoreCase)
                     || trimmed.StartsWith("passed_tests:", StringComparison.OrdinalIgnoreCase)
                     || trimmed.StartsWith("passed:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(trimmed[(trimmed.IndexOf(':') + 1)..].Trim(), out var p))
                    metrics.PassedTests = p;
            }
            else if (trimmed.StartsWith("failed_tests:", StringComparison.OrdinalIgnoreCase)
                     || trimmed.StartsWith("failed:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(trimmed[(trimmed.IndexOf(':') + 1)..].Trim(), out var f))
                    metrics.FailedTests = f;
            }
        }

        FallbackParseBuildSuccess(output, metrics);
    }

    /// <summary>
    /// Extracts build success from raw worker output by looking for common build result patterns.
    /// </summary>
    private static void FallbackParseBuildSuccess(string output, IterationMetrics metrics)
    {
        if (output.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(output, @"\|\s*Errors\s*\|\s*0\s*\|")
            || System.Text.RegularExpressions.Regex.IsMatch(output, @"✅\s*\*{0,2}Succeeded\*{0,2}", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(output, @"Build(?:\s+(?:Status|Result))?\s*:\s*(?:✅\s*)?PASS", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(output, @"\b0\s+error(?:s|\(s\))", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            metrics.BuildSuccess = true;
    }

    private async Task MarkGoalCompleted(GoalPipeline pipeline, CancellationToken ct)
    {
        pipeline.AdvanceTo(GoalPhase.Done);

        var completedMeta = new GoalUpdateMetadata
        {
            CompletedAt = pipeline.CompletedAt ?? DateTime.UtcNow,
            Iterations = pipeline.Iteration,
        };
        await _goalManager.UpdateGoalStatusAsync(pipeline.GoalId, GoalStatus.Completed, completedMeta, ct);
        await CommitGoalsToConfigRepoAsync($"Goal '{pipeline.GoalId}' completed", ct);
        await CleanupBrainSessionAsync(pipeline.GoalId);

        var duration = pipeline.CompletedAt.HasValue
            ? pipeline.CompletedAt.Value - pipeline.CreatedAt
            : TimeSpan.Zero;

        pipeline.Metrics.Iteration = pipeline.Iteration;
        pipeline.Metrics.Duration = duration;
        pipeline.Metrics.Verdict = "PASS";
        pipeline.Metrics.RetryCount = pipeline.ReviewRetries + pipeline.TestRetries;
        pipeline.Metrics.ReviewRetryCount = pipeline.ReviewRetries;
        pipeline.Metrics.TestRetryCount = pipeline.TestRetries;
        PopulateAgentsMdVersions(pipeline);
        _metricsTracker?.RecordIteration(pipeline.Metrics);
        await CommitMetricsToConfigRepoAsync(pipeline, ct);

        // Check for regression after recording metrics
        if (_metricsTracker is not null && _agentsManager is not null)
        {
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

        _logger.LogInformation(
            "🎉 Goal {GoalId} completed! Iterations={Iterations}, Duration={Duration:F1}min, " +
            "Tests={Passed}/{Total}, Coverage={Coverage:F1}%",
            pipeline.GoalId, pipeline.Iteration, duration.TotalMinutes,
            pipeline.Metrics.PassedTests, pipeline.Metrics.TotalTests,
            pipeline.Metrics.CoveragePercent);
    }

    private async Task MarkGoalFailed(GoalPipeline pipeline, string reason, CancellationToken ct)
    {
        pipeline.AdvanceTo(GoalPhase.Failed);

        var failedMeta = new GoalUpdateMetadata
        {
            CompletedAt = pipeline.CompletedAt ?? DateTime.UtcNow,
            Iterations = pipeline.Iteration,
            FailureReason = reason,
        };
        await _goalManager.UpdateGoalStatusAsync(pipeline.GoalId, GoalStatus.Failed, failedMeta, ct);
        await CommitGoalsToConfigRepoAsync($"Goal '{pipeline.GoalId}' failed: {reason}", ct);
        await CleanupBrainSessionAsync(pipeline.GoalId);

        var duration = pipeline.CompletedAt.HasValue
            ? pipeline.CompletedAt.Value - pipeline.CreatedAt
            : TimeSpan.Zero;

        pipeline.Metrics.Iteration = pipeline.Iteration;
        pipeline.Metrics.Duration = duration;
        pipeline.Metrics.Verdict = "FAIL";
        pipeline.Metrics.RetryCount = pipeline.ReviewRetries + pipeline.TestRetries;
        pipeline.Metrics.ReviewRetryCount = pipeline.ReviewRetries;
        pipeline.Metrics.TestRetryCount = pipeline.TestRetries;
        PopulateAgentsMdVersions(pipeline);
        _metricsTracker?.RecordIteration(pipeline.Metrics);
        await CommitMetricsToConfigRepoAsync(pipeline, ct);

        _logger.LogWarning("Goal {GoalId} failed: {Reason}", pipeline.GoalId, reason);
    }

    private async Task CleanupBrainSessionAsync(string goalId)
    {
        if (_brain is DistributedBrain brain)
        {
            try { await brain.CleanupGoalSessionAsync(goalId); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to cleanup Brain session for goal {GoalId}", goalId); }
        }
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
            foreach (var role in new[] { "coder", "tester", "reviewer", "improver", "orchestrator" })
            {
                var history = _agentsManager.GetHistory(role);
                pipeline.Metrics.AgentsMdVersions[role] = $"v{history.Length:D3}";
            }
        }
    }

    /// <summary>
    /// Determines which roles had their AGENTS.md modified this iteration by comparing
    /// current versions against the previous iteration's recorded versions.
    /// </summary>
    private List<string> GetModifiedRoles(IterationMetrics current)
    {
        var modified = new List<string>();
        var history = _metricsTracker!.History;

        // Need at least 2 entries: previous + current (just recorded)
        if (history.Count < 2)
            return modified;

        var previous = history[^2];

        foreach (var (role, currentVersion) in current.AgentsMdVersions)
        {
            if (!previous.AgentsMdVersions.TryGetValue(role, out var previousVersion)
                || currentVersion != previousVersion)
            {
                modified.Add(role);
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

            // Re-prime Brain session with conversation history
            if (_brain is DistributedBrain brain && pipeline.Conversation.Count > 0)
            {
                try
                {
                    await brain.ReprimeSessionAsync(pipeline, ct);
                    _logger.LogInformation("Re-primed Brain session for goal {GoalId} ({ConvCount} entries)",
                        pipeline.GoalId, pipeline.Conversation.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to re-prime Brain session for goal {GoalId}", pipeline.GoalId);
                }
            }

            // If the pipeline was mid-task (has ActiveTaskId), re-enqueue the task for reassignment
            if (pipeline.ActiveTaskId is not null && pipeline.Phase is not (GoalPhase.Done or GoalPhase.Failed))
            {
                _logger.LogInformation("Pipeline {GoalId} was mid-task ({TaskId}) — will await worker reconnection",
                    pipeline.GoalId, pipeline.ActiveTaskId);
            }
        }

        _logger.LogInformation("Restored {Count} pipeline(s): {GoalIds}",
            restored.Count, string.Join(", ", restored.Select(p => p.GoalId)));
    }

    private async Task DispatchNextGoalAsync(CancellationToken ct)
    {
        var goal = await _goalManager.GetNextGoalAsync(ct);
        if (goal is null)
            return;

        if (!_dispatchedGoals.TryAdd(goal.Id, true))
            return;

        _logger.LogInformation("Dispatching goal '{GoalId}': {Description}", goal.Id, goal.Description);

        // Mark goal as in_progress with started_at timestamp
        var startedMeta = new GoalUpdateMetadata { StartedAt = DateTime.UtcNow };
        await _goalManager.UpdateGoalStatusAsync(goal.Id, GoalStatus.InProgress, startedMeta, ct);
        await CommitGoalsToConfigRepoAsync($"Goal '{goal.Id}' started", ct);

        // Create a pipeline for this goal
        var maxRetries = _config?.Orchestrator?.MaxRetriesPerTask ?? Constants.DefaultMaxRetriesPerTask;
        var maxIterations = _config?.Orchestrator?.MaxIterations ?? Constants.DefaultMaxIterations;
        var pipeline = _pipelineManager.CreatePipeline(goal, maxRetries, maxIterations);

        if (_brain is not null)
        {
            // Brain-powered: ask the Brain to plan the goal
            var goalPlan = await _brain.PlanGoalAsync(pipeline, ct);

            // Get the iteration plan from the Brain
            IterationPlan iterationPlan;
            try
            {
                iterationPlan = await _brain.PlanIterationAsync(pipeline, ct);
                iterationPlan = ValidatePlan(iterationPlan);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Brain failed to plan iteration for {GoalId}, using default plan", pipeline.GoalId);
                iterationPlan = IterationPlan.Default();
            }
            pipeline.SetPlan(iterationPlan);

            var firstRole = goalPlan.Action switch
            {
                OrchestratorActionType.SpawnReviewer => WorkerRole.Reviewer,
                OrchestratorActionType.SpawnTester => WorkerRole.Tester,
                OrchestratorActionType.SpawnImprover => WorkerRole.Improver,
                _ => WorkerRole.Coder,
            };

            pipeline.AdvanceTo(firstRole == WorkerRole.Coder ? GoalPhase.Coding : GoalPhase.Review);
            await DispatchToRole(pipeline, firstRole, goalPlan.Prompt, ct);
        }
        else
        {
            // Mechanical fallback: just dispatch to coder with default plan
            pipeline.SetPlan(IterationPlan.Default());
            pipeline.AdvanceTo(GoalPhase.Coding);
            await DispatchToRole(pipeline, WorkerRole.Coder, BuildCoderPrompt(goal), ct);
        }

        _pipelineManager.PersistFull(pipeline);
    }

    private List<TargetRepository> ResolveRepositories(Goal goal)
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
                _logger.LogWarning("Repository '{RepoName}' from goal not found in config", repoName);
            }
        }

        return repos;
    }

    private static string BuildCoderPrompt(Goal goal)
    {
        return $"""
            You are a coder working on a software project. Your task:

            {goal.Description}

            Instructions:
            - Make the necessary code changes to accomplish this goal.
            - Follow existing code conventions and style.
            - Commit your changes with a clear, descriptive commit message.
            - Only make changes directly related to the goal.
            """;
    }

    private static string InjectTokenIntoUrl(string url)
    {
        var token = Environment.GetEnvironmentVariable("GH_TOKEN");
        if (string.IsNullOrEmpty(token) || !url.StartsWith("https://github.com/"))
            return url;

        return url.Replace("https://github.com/", $"https://x-access-token:{token}@github.com/");
    }
}
