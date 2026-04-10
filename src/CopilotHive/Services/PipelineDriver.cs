using CopilotHive.Agents;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Improvement;
using CopilotHive.Metrics;
using CopilotHive.Orchestration;
using CopilotHive.Workers;

namespace CopilotHive.Services;

/// <summary>
/// Drives the pipeline state machine: phase transitions, new iterations, merge failure handling,
/// phase dispatching, and merge execution.
/// Extracted from <see cref="GoalDispatcher"/> — all logic is identical.
/// Callbacks to GoalDispatcher are passed as Func delegates to avoid circular DI.
/// </summary>
internal sealed class PipelineDriver
{
    private readonly IDistributedBrain? _brain;
    private readonly GoalLifecycleService _lifecycleService;
    private readonly GoalManager _goalManager;
    private readonly IBrainRepoManager _repoManager;
    private readonly ImprovementAnalyzer? _improvementAnalyzer;
    private readonly AgentsManager? _agentsManager;
    private readonly MetricsTracker? _metricsTracker;
    private readonly ILogger _logger;

    // Callbacks into GoalDispatcher
    private readonly Func<GoalPipeline, WorkerRole, string?, CancellationToken, Task> _dispatchToRole;
    private readonly Func<GoalPipeline, GoalPhase, string?, CancellationToken, Task<string>> _resolvePrompt;
    private readonly Func<GoalPipeline, string?, CancellationToken, Task<IterationPlan>> _resolvePlan;
    private readonly Func<Goal, List<TargetRepository>> _resolveRepositories;
    private readonly Func<CancellationToken, Task> _syncAgents;
    private readonly Func<GoalPipeline, CancellationToken, Task<string>> _generateMergeCommitMessage;

    public PipelineDriver(
        IDistributedBrain? brain,
        GoalLifecycleService lifecycleService,
        GoalManager goalManager,
        IBrainRepoManager repoManager,
        ImprovementAnalyzer? improvementAnalyzer,
        AgentsManager? agentsManager,
        MetricsTracker? metricsTracker,
        Func<GoalPipeline, WorkerRole, string?, CancellationToken, Task> dispatchToRole,
        Func<GoalPipeline, GoalPhase, string?, CancellationToken, Task<string>> resolvePrompt,
        Func<GoalPipeline, string?, CancellationToken, Task<IterationPlan>> resolvePlan,
        Func<Goal, List<TargetRepository>> resolveRepositories,
        Func<CancellationToken, Task> syncAgents,
        Func<GoalPipeline, CancellationToken, Task<string>> generateMergeCommitMessage,
        ILogger logger)
    {
        _brain = brain;
        _lifecycleService = lifecycleService;
        _goalManager = goalManager;
        _repoManager = repoManager;
        _improvementAnalyzer = improvementAnalyzer;
        _agentsManager = agentsManager;
        _metricsTracker = metricsTracker;
        _dispatchToRole = dispatchToRole;
        _resolvePrompt = resolvePrompt;
        _resolvePlan = resolvePlan;
        _resolveRepositories = resolveRepositories;
        _syncAgents = syncAgents;
        _generateMergeCommitMessage = generateMergeCommitMessage;
        _logger = logger;
    }

    public async Task DriveNextPhaseAsync(GoalPipeline pipeline, TaskResult result, CancellationToken ct)
    {
        // Early-exit guard: a crashed/failed worker should not continue through the pipeline.
        // Recording the crash output as normal output would pollute iteration data.
        if (result.Status == TaskOutcome.Failed)
        {
            var truncatedOutput = result.Output.Length > 300 ? result.Output[..300] + "..." : result.Output;
            _logger.LogError("Worker for goal {GoalId} failed with output: {Output}", pipeline.GoalId, result.Output);
            await _lifecycleService.MarkGoalFailedAsync(pipeline, $"Worker failed: {truncatedOutput}", ct);
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
                await _lifecycleService.MarkGoalFailedAsync(pipeline, "Coder produced no file changes after max iterations (no-op)", ct);
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
                ? await _resolvePrompt(pipeline, GoalPhase.Coding, noOpContext, ct)
                : $"Work on: {pipeline.Description}. {noOpContext}";
            await _dispatchToRole(pipeline, WorkerRole.Coder, retryPrompt, ct);
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
        var outputSummary = PipelineHelpers.BuildWorkerOutputSummary(pipeline.Phase, verdict, result);
        pipeline.Conversation.Add(new ConversationEntry(workerRole, outputSummary, pipeline.Iteration, "worker-output"));

        // After Improver: sync config repo to pick up the changes it pushed directly
        if (pipeline.Phase == GoalPhase.Improve)
        {
            _logger.LogInformation("Improver completed for goal {GoalId} — syncing config repo for updated agents.md files",
                pipeline.GoalId);
            await _syncAgents(ct);
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
            var workerOutput = !string.IsNullOrWhiteSpace(result.Metrics?.Summary)
                ? result.Metrics.Summary
                : result.Output;
            logEntry.WorkerOutput = workerOutput.Length > 4000
                ? workerOutput[..4000] + $"... ({workerOutput.Length} chars total)"
                : workerOutput;
            logEntry.Result = phaseInput == PhaseInput.Succeeded ? PhaseOutcome.Pass : PhaseOutcome.Fail;
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
                await _lifecycleService.MarkGoalCompletedAsync(pipeline, ct);
                break;
        }
    }

    public async Task HandleNewIterationAsync(
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
            await _lifecycleService.MarkGoalFailedAsync(pipeline,
                $"Exceeded max {(isReviewRelated ? "review" : "test")} retries", ct);
            return;
        }

        pipeline.AdvanceTo(GoalPhase.Coding);

        // Snapshot the ending iteration from PhaseLog
        var iterationSummary = PipelineHelpers.BuildIterationSummary(pipeline);
        pipeline.CompletedIterationSummaries.Add(iterationSummary);

        // Persist the iteration summary to the goal source so the dashboard can read it
        var updateMeta = new GoalUpdateMetadata { IterationSummary = iterationSummary };
        await _goalManager.UpdateGoalStatusAsync(pipeline.GoalId, GoalStatus.InProgress, updateMeta, ct);

        if (!pipeline.IterationBudget.TryConsume())
        {
            pipeline.StateMachine.Fail();
            pipeline.AdvanceTo(GoalPhase.Failed);
            await _lifecycleService.MarkGoalFailedAsync(pipeline, "Exceeded max iterations", ct);
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
                var rawPlan = await _resolvePlan(pipeline, null, ct);
                var originalPhases = rawPlan.Phases.ToList();
                newPlan = IterationPlanValidator.ValidatePlan(rawPlan);

                if (!originalPhases.SequenceEqual(newPlan.Phases))
                {
                    var note = IterationPlanValidator.BuildPlanAdjustmentNote(originalPhases, newPlan.Phases);
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
            ? await _resolvePrompt(pipeline, GoalPhase.Coding, context, ct)
            : $"Fix issues for: {pipeline.Description}. {context}";

        // PhaseLog: append a new entry for the coder phase in the new iteration
        pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 1));
        if (pipeline.CurrentPhaseEntry is { } newIterEntry)
        {
            newIterEntry.WorkerPrompt = fixPrompt;
            newIterEntry.BrainPrompt = PipelineHelpers.GetLastCraftPromptFromConversation(pipeline);
            // Capture planning prompt/response from conversation onto the first entry of the new iteration
            var (planningPrompt, planningResponse) = PipelineHelpers.GetPlanningPromptsFromConversation(pipeline);
            newIterEntry.PlanningPrompt = planningPrompt;
            newIterEntry.PlanningResponse = planningResponse;
        }

        await _dispatchToRole(pipeline, WorkerRole.Coder, fixPrompt, ct);
    }

    public async Task HandleMergeFailureAsync(GoalPipeline pipeline, string errorMessage, CancellationToken ct)
    {
        // State machine already transitioned to Coding (NewIteration) before this is called.
        // Check retry/iteration limits.
        if (!pipeline.ReviewRetryBudget.TryConsume())
        {
            pipeline.StateMachine.Fail();
            pipeline.AdvanceTo(GoalPhase.Failed);
            await _lifecycleService.MarkGoalFailedAsync(pipeline, $"Merge failed after max retries: {errorMessage}", ct);
            return;
        }

        _logger.LogInformation(
            "Merge conflict for goal {GoalId} — sending back to Coder for rebase (retry {Retry}/{Max})",
            pipeline.GoalId, pipeline.ReviewRetryBudget.Used, pipeline.ReviewRetryBudget.Allowed);

        if (!pipeline.IterationBudget.TryConsume())
        {
            pipeline.StateMachine.Fail();
            pipeline.AdvanceTo(GoalPhase.Failed);
            await _lifecycleService.MarkGoalFailedAsync(pipeline, "Exceeded max iterations during merge conflict resolution", ct);
            return;
        }
        pipeline.AdvanceTo(GoalPhase.Coding);

        var repos = _resolveRepositories(pipeline.Goal);
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
                var rawPlan = await _resolvePlan(pipeline, null, ct);
                var originalPhases = rawPlan.Phases.ToList();
                newPlan = IterationPlanValidator.ValidatePlan(rawPlan);

                if (!originalPhases.SequenceEqual(newPlan.Phases))
                {
                    var note = IterationPlanValidator.BuildPlanAdjustmentNote(originalPhases, newPlan.Phases);
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
            ? await _resolvePrompt(pipeline, GoalPhase.Coding, rebaseContext, ct)
            : rebaseContext;
        await _dispatchToRole(pipeline, WorkerRole.Coder, fixPrompt, ct);
    }

    /// <summary>Dispatch a specific pipeline phase to the appropriate worker.</summary>
    public async Task DispatchPhaseAsync(
        GoalPipeline pipeline, GoalPhase phase, string? phaseInstructions, CancellationToken ct, int occurrence = 1)
    {
        // PhaseLog: append a new entry when the phase starts
        pipeline.PhaseLog.Add(PhaseResult.Create(phase, pipeline.Iteration, occurrence));

        switch (phase)
        {
            case GoalPhase.Coding:
            case GoalPhase.Review:
            case GoalPhase.Testing:
            case GoalPhase.DocWriting:
                var prompt = _brain is not null
                    ? await _resolvePrompt(pipeline, phase, null, ct)
                    : $"Work on: {pipeline.Description} (phase: {phase})";
                if (pipeline.CurrentPhaseEntry is { } promptEntry)
                {
                    promptEntry.WorkerPrompt = prompt;
                    promptEntry.BrainPrompt = PipelineHelpers.GetLastCraftPromptFromConversation(pipeline);
                }
                await _dispatchToRole(pipeline, phase.ToWorkerRole(), prompt, ct);
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
                    await _lifecycleService.CommitGoalsToConfigRepoAsync(
                        $"Goal '{pipeline.GoalId}': improver skipped ({ex.GetType().Name})", ct);

                    // Advance past the failed Improve phase (non-blocking in state machine)
                    var skipResult = pipeline.StateMachine.Transition(PhaseInput.Failed);
                    pipeline.AdvanceTo(skipResult.NextPhase);
                    if (skipResult.Effect == TransitionEffect.Continue)
                        await DispatchPhaseAsync(pipeline, skipResult.NextPhase, null, ct);
                    else if (skipResult.Effect == TransitionEffect.Completed)
                        await _lifecycleService.MarkGoalCompletedAsync(pipeline, ct);
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
        await _syncAgents(ct);

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
            ? await _resolvePrompt(pipeline, GoalPhase.Improve, improveContext, ct)
            : "Update the *.agents.md files based on iteration results.\n\n" + analysis;
        if (pipeline.CurrentPhaseEntry is { } improveEntry)
        {
            improveEntry.WorkerPrompt = improvePrompt;
            improveEntry.BrainPrompt = PipelineHelpers.GetLastCraftPromptFromConversation(pipeline);
        }
        await _dispatchToRole(pipeline, WorkerRole.Improver, improvePrompt, ct);
    }

    private async Task PerformMergeAsync(GoalPipeline pipeline, CancellationToken ct)
    {
        if (pipeline.CoderBranch is null)
        {
            await _lifecycleService.MarkGoalFailedAsync(pipeline, "No coder branch set", ct);
            return;
        }

        _logger.LogInformation("Merging branch {Branch} for goal {GoalId}", pipeline.CoderBranch, pipeline.GoalId);

        try
        {
            var repos = _resolveRepositories(pipeline.Goal);
            var commitMessage = await _generateMergeCommitMessage(pipeline, ct);
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

            await _lifecycleService.MarkGoalCompletedAsync(pipeline, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Merge failed for goal {GoalId} — checking if retryable", pipeline.GoalId);

            // State machine: merge failed → NewIteration (back to Coding)
            var mergeResult = pipeline.StateMachine.Transition(PhaseInput.Failed);
            if (mergeResult.Effect == TransitionEffect.NewIteration)
                await HandleMergeFailureAsync(pipeline, ex.Message, ct);
            else
                await _lifecycleService.MarkGoalFailedAsync(pipeline, $"Unexpected merge failure effect: {mergeResult.Effect}", ct);
        }
    }
}
