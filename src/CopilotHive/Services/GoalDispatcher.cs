using System.Collections.Concurrent;
using CopilotHive.Agents;
using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Improvement;
using CopilotHive.Metrics;
using CopilotHive.Orchestration;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;
using GrpcWorkerRole = CopilotHive.Shared.Grpc.WorkerRole;
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

        completionNotifier.OnTaskCompleted+= complete => HandleTaskCompletionAsync(complete);
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

        // Guard: ignore late-arriving completions for goals already finished
        if (pipeline.Phase is GoalPhase.Done or GoalPhase.Failed)
        {
            _logger.LogInformation(
                "Task {TaskId} completed but goal {GoalId} already {Phase} — ignoring duplicate",
                complete.TaskId, pipeline.GoalId, pipeline.Phase);
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

            var retryPrompt = await _brain!.CraftPromptAsync(pipeline, WorkerRole.Coder, noOpContext, ct);
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
            pipeline.Phase,
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

            // Validate agents.md file sizes; re-dispatch improver inline if any exceed the 4000 char limit.
            // The while loop retries up to 3 times per file and is self-contained — no early return.
            if (_configRepo is not null)
            {
                const int MaxAgentsMdChars = 4000;
                const int MaxImproverRetries = 3;

                foreach (var role in WorkerRoles.AgentRoles)
                {
                    var agentsMdPath = Path.Combine(_configRepo.LocalPath, "agents", $"{role.ToRoleName()}.agents.md");
                    if (!File.Exists(agentsMdPath)) continue;

                    var content = await File.ReadAllTextAsync(agentsMdPath, ct);
                    while (content.Length > MaxAgentsMdChars && pipeline.ImproverRetries < MaxImproverRetries)
                    {
                        pipeline.IncrementImproverRetry();
                        _logger.LogWarning(
                            "agents.md for '{Role}' is {Count} chars, exceeds 4000 limit. Retry {Attempt}/3.",
                            role, content.Length, pipeline.ImproverRetries);

                        var retryPrompt = $"Your updated {role.ToRoleName()}.agents.md is {content.Length} characters, which exceeds the 4000 character limit. The Copilot CLI will discard content beyond this limit. Please condense the file to under 4000 characters while keeping the most impactful rules. Remove verbose examples, merge similar rules, and use concise language.";
                        await DispatchToRole(pipeline, WorkerRole.Improver, retryPrompt, ct);
                        await SyncAgentsFromConfigRepoAsync(ct);
                        content = await File.ReadAllTextAsync(agentsMdPath, ct);
                    }

                    if (content.Length > MaxAgentsMdChars)
                    {
                        _logger.LogWarning(
                            "agents.md for '{Role}' still exceeds 4000 chars after 3 retries. Discarding improver changes.",
                            role);
                        await RestoreAgentsMdFromGitAsync(role, pipeline.PreImproverSha, ct);
                    }
                }
            }
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
            pipeline.Metrics.ReviewVerdict = interpretation.Verdict == "FAIL"
                ? "REQUEST_CHANGES"
                : interpretation.Verdict == "PASS"
                    ? "APPROVE"
                    : interpretation.ReviewVerdict ?? "";
            if (interpretation.Issues is { Count: > 0 })
            {
                pipeline.Metrics.ReviewIssuesFound += interpretation.Issues.Count;
                pipeline.Metrics.ReviewIssues.AddRange(interpretation.Issues);
            }
        }

        if (interpretation.Issues is not null)
            pipeline.Metrics.Issues.AddRange(interpretation.Issues);

        // Propagate model_tier using first-non-null-wins — don't overwrite a tier already set by an earlier Brain call.
        DistributedBrain.ApplyModelTierIfNotSet(pipeline, interpretation.ModelTier);

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

            case OrchestratorActionType.SpawnDocWriter:
                pipeline.AdvanceTo(GoalPhase.DocWriting);
                await DispatchToRole(pipeline, WorkerRole.DocWriter, interpretation.Prompt, ct);
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

                var fixPrompt = await _brain.CraftPromptAsync(pipeline, WorkerRole.Coder, fixContext, ct);
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
                var retryPrompt = await _brain.CraftPromptAsync(pipeline, WorkerRole.Coder,
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
                ? await _brain.CraftPromptAsync(pipeline, WorkerRole.Coder,
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
                    ? await _brain.CraftPromptAsync(pipeline, WorkerRole.Reviewer, phaseInstructions, ct)
                    : $"Review the code changes for: {pipeline.Description}";
                await DispatchToRole(pipeline, WorkerRole.Reviewer, reviewPrompt, ct);
                break;

            case GoalPhase.Testing:
                pipeline.AdvanceTo(GoalPhase.Testing);
                var testPrompt = _brain is not null
                    ? await _brain.CraftPromptAsync(pipeline, WorkerRole.Tester, phaseInstructions, ct)
                    : $"Run the existing tests and verify the changes for: {pipeline.Description}";
                await DispatchToRole(pipeline, WorkerRole.Tester, testPrompt, ct);
                break;

            case GoalPhase.DocWriting:
                pipeline.AdvanceTo(GoalPhase.DocWriting);
                var docPrompt = BuildDocWriterPrompt(pipeline, phaseInstructions);
                await DispatchToRole(pipeline, WorkerRole.DocWriter, docPrompt, ct);
                break;

            case GoalPhase.Improve:
                pipeline.AdvanceTo(GoalPhase.Improve);
                _logger.LogInformation("Dispatching Improver for goal {GoalId}", pipeline.GoalId);

                // Pull the config repo to ensure the improver container starts with the latest agents.md files
                await SyncAgentsFromConfigRepoAsync(ct);

                // Capture HEAD SHA before any improver changes so we can restore if all retries fail
                if (_configRepo is not null)
                    pipeline.PreImproverSha = await GetCurrentCommitShaAsync(_configRepo.LocalPath, ct);

                var analysis = "";
                if (_improvementAnalyzer is not null && _agentsManager is not null && _metricsTracker is not null)
                {
                    var agentsMd = new Dictionary<string, string>();
                    foreach (var role in WorkerRoles.AgentRoles)
                    {
                        var roleName = role.ToRoleName();
                        var content = _agentsManager.GetAgentsMd(roleName);
                        if (!string.IsNullOrEmpty(content)) agentsMd[roleName] = content;
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

                var telemetryAggregator = new TelemetryAggregator();
                var telemetryRoleNames = WorkerRoles.TelemetryRoles.Select(r => r.ToRoleName());
                var stateDir = Environment.GetEnvironmentVariable("STATE_DIR") ?? "/app/state";
                var telemetrySummary = telemetryAggregator.AggregateTelemetry(stateDir, telemetryRoleNames);
                var telemetryText = telemetryAggregator.FormatSummary(telemetrySummary);
                if (!string.IsNullOrEmpty(telemetryText))
                    improveContext += "\n\n## Telemetry\n" + telemetryText;
                telemetryAggregator.ClearTelemetryFiles(stateDir, telemetryRoleNames);

                var improvePrompt = _brain is not null
                    ? await _brain.CraftPromptAsync(pipeline, WorkerRole.Improver, improveContext, ct)
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
        // If no prompt provided (Brain might have returned null), craft one.
        // Preserve the current model tier first — CraftPromptAsync may reset it to "standard",
        // silently losing a "premium" tier that was set by a prior Brain decision.
        if (string.IsNullOrWhiteSpace(prompt) && _brain is not null)
        {
            var preservedTier = pipeline.LatestModelTier;
            prompt = await _brain.CraftPromptAsync(pipeline, role, null, ct);
            // Always restore the preserved tier — CraftPromptAsync is only used for prompt text,
            // and must never influence model_tier.
            pipeline.LatestModelTier = preservedTier;
        }

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

        // Resolve per-role model from config; upgrade to premium when the Brain requested it
        var roleName = role.ToRoleName();
        var model = _config?.GetModelForRole(roleName);
        if (pipeline.LatestModelTier == "premium" && _config is not null)
        {
            var premiumModel = _config.GetPremiumModelForRole(roleName);
            if (premiumModel is not null)
                model = premiumModel;
        }
        _logger.LogDebug("Model for {Role}: {Model} (tier={Tier}, configLoaded={ConfigLoaded})",
            roleName, model ?? "(null)", pipeline.LatestModelTier, _config is not null);

        var grpcRole = role.ToGrpcRole();
        var task = _taskBuilder.Build(
            goalId: pipeline.GoalId,
            goalDescription: pipeline.Description,
            role: grpcRole,
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
        var idleWorker = _workerPool.GetIdleWorker(grpcRole);
        if (idleWorker is not null)
        {
            var queuedTask = _taskQueue.TryDequeue(grpcRole);
            if (queuedTask is null && idleWorker.IsGeneric)
                queuedTask = _taskQueue.TryDequeue(GrpcWorkerRole.Unspecified);

            if (queuedTask is not null)
            {
                // For generic workers, set their role and send agents.md
                if (idleWorker.IsGeneric)
                {
                    idleWorker.Role = queuedTask.Role;
                    var taskRoleName = queuedTask.Role.ToString().ToLowerInvariant();
                    _logger.LogInformation("Generic worker {WorkerId} assigned role {Role} for task {TaskId}",
                        idleWorker.Id, taskRoleName, queuedTask.TaskId);
                    await SendAgentsMdToWorkerAsync(idleWorker, taskRoleName, ct);
                }

                _taskQueue.Activate(queuedTask, idleWorker.Id);
                _workerPool.MarkBusy(idleWorker.Id, queuedTask.TaskId);
                await idleWorker.MessageChannel.Writer.WriteAsync(
                    new OrchestratorMessage { Assignment = queuedTask }, ct);
                _logger.LogInformation("Task {TaskId} pushed to worker {WorkerId}", queuedTask.TaskId, idleWorker.Id);
            }
        }
    }

    private async Task SendAgentsMdToWorkerAsync(ConnectedWorker worker, string roleName, CancellationToken ct)
    {
        if (_agentsManager is null) return;
        var content = _agentsManager.GetAgentsMd(roleName);
        if (string.IsNullOrEmpty(content)) return;

        try
        {
            await worker.MessageChannel.Writer.WriteAsync(
                new OrchestratorMessage
                {
                    UpdateAgents = new UpdateAgents
                    {
                        AgentsMdContent = content,
                        Role = roleName,
                    }
                }, ct);
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
            foreach (var repo in repos)
            {
                var tempDir = Path.Combine(Path.GetTempPath(), $"hive-merge-{Guid.NewGuid():N}");
                try
                {
                    await RunGitAsync($"clone --branch {repo.DefaultBranch} {repo.Url} {tempDir}", ct);
                    await RunGitAsync($"-C {tempDir} config user.email copilothive@local", ct);
                    await RunGitAsync($"-C {tempDir} config user.name CopilotHive", ct);
                    await RunGitAsync($"-C {tempDir} fetch origin {pipeline.CoderBranch}", ct);

                    try
                    {
                        await RunGitAsync($"-C {tempDir} merge origin/{pipeline.CoderBranch} --no-edit", ct);
                    }
                    catch (Exception mergeEx)
                    {
                        // Merge failed — attempt automatic rebase before falling back to coder
                        _logger.LogInformation(
                            "Merge conflict for {GoalId} — attempting automatic rebase of {Branch} onto {Base}",
                            pipeline.GoalId, pipeline.CoderBranch, repo.DefaultBranch);

                        // Abort the failed merge, then try rebase in a fresh clone of the feature branch
                        await RunGitAsync($"-C {tempDir} merge --abort", ct);

                        var rebaseDir = Path.Combine(Path.GetTempPath(), $"hive-rebase-{Guid.NewGuid():N}");
                        try
                        {
                            await RunGitAsync($"clone --branch {pipeline.CoderBranch} {repo.Url} {rebaseDir}", ct);
                            await RunGitAsync($"-C {rebaseDir} config user.email copilothive@local", ct);
                            await RunGitAsync($"-C {rebaseDir} config user.name CopilotHive", ct);
                            await RunGitAsync($"-C {rebaseDir} fetch origin {repo.DefaultBranch}", ct);
                            await RunGitAsync($"-C {rebaseDir} rebase origin/{repo.DefaultBranch}", ct);

                            // Rebase succeeded — force-push the rebased branch and retry merge
                            await RunGitAsync($"-C {rebaseDir} push origin {pipeline.CoderBranch} --force-with-lease", ct);
                            _logger.LogInformation("Auto-rebase succeeded for {GoalId} — retrying merge", pipeline.GoalId);

                            // Retry merge in the original temp dir
                            await RunGitAsync($"-C {tempDir} fetch origin {pipeline.CoderBranch}", ct);
                            await RunGitAsync($"-C {tempDir} merge origin/{pipeline.CoderBranch} --no-edit", ct);
                        }
                        catch (Exception rebaseEx)
                        {
                            // Auto-rebase failed — abort and fall through to coder-based resolution
                            _logger.LogWarning(
                                "Auto-rebase failed for {GoalId} — falling back to coder: {Error}",
                                pipeline.GoalId, rebaseEx.Message);
                            try { await RunGitAsync($"-C {rebaseDir} rebase --abort", ct); } catch { }
                            throw new InvalidOperationException(
                                $"Merge conflict (auto-rebase failed): {mergeEx.Message}", mergeEx);
                        }
                        finally
                        {
                            if (Directory.Exists(rebaseDir))
                            {
                                try { Directory.Delete(rebaseDir, true); }
                                catch { /* Best effort cleanup */ }
                            }
                        }
                    }

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
    /// (Coding → Testing → Review → Merging) then runs again.
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
            4. Build and test to verify everything works
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
            ? await _brain.CraftPromptAsync(pipeline, WorkerRole.Coder, rebaseContext, ct)
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

    private async Task RestoreAgentsMdFromGitAsync(WorkerRole role, string? sha, CancellationToken ct)
    {
        if (_configRepo is null) return;

        var roleName = role.ToRoleName();
        var gitRef = string.IsNullOrWhiteSpace(sha) ? "HEAD~1" : sha;
        var psi = new System.Diagnostics.ProcessStartInfo("git", $"checkout {gitRef} -- agents/{roleName}.agents.md")
        {
            WorkingDirectory = _configRepo.LocalPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            _logger.LogWarning("Failed to restore {Role} agents.md from git: {Error}", role, await stderrTask);
        }
    }

    private async Task<string> GetCurrentCommitShaAsync(string repoPath, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", $"-C {repoPath} rev-parse HEAD")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return stdout.Trim();
    }

    /// <summary>
    /// Sends an UpdateAgents message to all connected workers whose role matches the given role string.
    /// Best-effort: failures are logged but do not block the pipeline.
    /// </summary>
    private async Task BroadcastAgentsUpdateAsync(WorkerRole role, string content, CancellationToken ct)
    {
        var grpcRole = role.ToGrpcRole();
        var workers = _workerPool.GetAllWorkers()
            .Where(w => w.Role == grpcRole);

        var roleName = role.ToRoleName();
        var message = new OrchestratorMessage
        {
            UpdateAgents = new UpdateAgents
            {
                AgentsMdContent = content,
                Role = roleName,
            }
        };

        foreach (var worker in workers)
        {
            try
            {
                await worker.MessageChannel.Writer.WriteAsync(message, ct);
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
                var repoContent = await _configRepo.LoadAgentsMdAsync(roleName, ct);
                if (string.IsNullOrEmpty(repoContent)) continue;

                var currentContent = _agentsManager.GetAgentsMd(roleName);
                if (repoContent == currentContent) continue;

                _agentsManager.UpdateAgentsMd(roleName, repoContent);

                // Only broadcast to Docker workers — Orchestrator/MergeWorker have no gRPC equivalent
                if (WorkerRoles.BroadcastableRoles.Contains(role))
                {
                    await BroadcastAgentsUpdateAsync(role, repoContent, ct);
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
    /// Delegates to <see cref="DistributedBrain.FallbackParseTestMetrics"/> for the actual parsing
    /// and maps the result onto the mutable <paramref name="metrics"/> object.
    /// </summary>
    internal static void FallbackParseTestMetrics(string output, IterationMetrics metrics)
    {
        var extracted = DistributedBrain.FallbackParseTestMetrics(output);
        if (extracted is null)
            return;

        if (extracted.TotalTests.HasValue)
            metrics.TotalTests = Math.Max(metrics.TotalTests, extracted.TotalTests.Value);
        if (extracted.PassedTests.HasValue)
            metrics.PassedTests = Math.Max(metrics.PassedTests, extracted.PassedTests.Value);
        if (extracted.FailedTests.HasValue)
            metrics.FailedTests = Math.Max(metrics.FailedTests, extracted.FailedTests.Value);
        if (extracted.BuildSuccess is true)
            metrics.BuildSuccess = true;
        if (extracted.CoveragePercent.HasValue)
            metrics.CoveragePercent = Math.Max(metrics.CoveragePercent, extracted.CoveragePercent.Value);
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
            foreach (var role in WorkerRoles.AgentRoles)
            {
                var roleName = role.ToRoleName();
                var history = _agentsManager.GetHistory(roleName);
                pipeline.Metrics.AgentsMdVersions[roleName] = $"v{history.Length:D3}";
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

            var goalAction = goalPlan.Action;
            switch (goalAction)
            {
                case OrchestratorActionType.Done:
                case OrchestratorActionType.Skip:
                    _logger.LogInformation("Brain decided to {Action} goal {GoalId} at planning stage",
                        goalAction, pipeline.GoalId);
                    await MarkGoalCompleted(pipeline, ct);
                    break;

                case OrchestratorActionType.SpawnCoder:
                case OrchestratorActionType.SpawnReviewer:
                case OrchestratorActionType.SpawnTester:
                case OrchestratorActionType.SpawnImprover:
                case OrchestratorActionType.SpawnDocWriter:
                    var (firstRole, firstPhase) = goalAction switch
                    {
                        OrchestratorActionType.SpawnCoder => (WorkerRole.Coder, GoalPhase.Coding),
                        OrchestratorActionType.SpawnReviewer => (WorkerRole.Reviewer, GoalPhase.Review),
                        OrchestratorActionType.SpawnTester => (WorkerRole.Tester, GoalPhase.Testing),
                        OrchestratorActionType.SpawnImprover => (WorkerRole.Improver, GoalPhase.Improve),
                        OrchestratorActionType.SpawnDocWriter => (WorkerRole.DocWriter, GoalPhase.DocWriting),
                        _ => throw new InvalidOperationException($"Unhandled spawn action: {goalAction}"),
                    };

                    pipeline.AdvanceTo(firstPhase);
                    DistributedBrain.ApplyModelTierIfNotSet(pipeline, goalPlan.ModelTier);
                    await DispatchToRole(pipeline, firstRole, goalPlan.Prompt, ct);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unhandled Brain action '{goalAction}' during goal planning for '{pipeline.GoalId}'");
            }
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
            3. Use the /build skill to build the project and fix any errors
            4. Use the /test skill to run the tests and fix any failures
            5. Run `git add` + `git commit` with a descriptive message
            6. Verify with `git diff origin/<base-branch>...HEAD --stat` that you have a non-empty diff

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

        var branch = pipeline.CoderBranch
            ?? throw new InvalidOperationException("CoderBranch must be set before building doc-writer prompt");

        return $"""
            You are the doc-writer. Your ONLY job is to update documentation for the code changes
            that have already been made on branch {branch}.

            Goal summary: {pipeline.Description}
            {additionalContext}
            Your tasks (do ALL of these):
            1. Run `git diff origin/<base-branch>...HEAD --stat` to see ALL files changed on this branch
            2. Run `git diff origin/<base-branch>...HEAD` to review the full diff
            3. Update the CHANGELOG.md — add entries under [Unreleased] describing what was added/changed/fixed
            4. Update XML doc comments (`<summary>`, `<param>`, `<returns>`) on any new or changed public APIs
            5. Update README.md if the changes affect user-facing features or configuration
            6. Use the /build skill to verify your doc comment changes compile
            7. Run `git add` + `git commit` with message "docs: update documentation for [brief description]"

            CRITICAL RULES:
            - Do NOT write or run tests — that is the tester's job
            - Do NOT implement features or fix bugs — that is the coder's job
            - Do NOT run git push — the orchestrator handles that
            - Focus ONLY on documentation artifacts (CHANGELOG, README, XML doc comments)

            When done, produce a DOC_REPORT block:
            ```DOC_REPORT
            files_updated: [list of files you changed]
            changelog_entries: [number of entries added]
            verdict: PASS or FAIL
            issues: [any issues encountered]
            ```
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
