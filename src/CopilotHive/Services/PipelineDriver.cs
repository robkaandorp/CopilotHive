using CopilotHive.Agents;
using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Improvement;
using CopilotHive.Knowledge;
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
    /// <summary>
    /// Replaces every character that could break a log line into multiple lines with <c>?</c>,
    /// so an unusual or malicious changed-file path cannot inject fake log entries.
    /// Delegates to <see cref="LogSanitizer.SanitizePath"/> so the worker-side and
    /// orchestrator-side log sites share one definition of "unsafe".
    /// </summary>
    internal static string SanitizeLogPath(string path) => LogSanitizer.SanitizePath(path);

    private readonly IDistributedBrain? _brain;
    private readonly GoalLifecycleService _lifecycleService;
    private readonly GoalManager _goalManager;
    private readonly IBrainRepoManager _repoManager;
    private readonly ImprovementAnalyzer? _improvementAnalyzer;
    private readonly AgentsManager? _agentsManager;
    private readonly MetricsTracker? _metricsTracker;
    private readonly ILogger _logger;
    private readonly KnowledgeGraph? _knowledgeGraph;
    private readonly ConfigRepoManager? _configRepo;

    // Callbacks into GoalDispatcher
    private readonly Func<GoalPipeline, WorkerRole, string?, CancellationToken, Task> _dispatchToRole;
    private readonly Func<GoalPipeline, GoalPhase, string?, CancellationToken, Task<string>> _resolvePrompt;
    private readonly Func<GoalPipeline, string?, CancellationToken, Task<PlanResult>> _resolvePlan;
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
        Func<GoalPipeline, string?, CancellationToken, Task<PlanResult>> resolvePlan,
        Func<Goal, List<TargetRepository>> resolveRepositories,
        Func<CancellationToken, Task> syncAgents,
        Func<GoalPipeline, CancellationToken, Task<string>> generateMergeCommitMessage,
        ILogger logger,
        KnowledgeGraph? knowledgeGraph = null,
        ConfigRepoManager? configRepo = null)
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
        _knowledgeGraph = knowledgeGraph;
        _configRepo = configRepo;
    }

    /// <summary>
    /// Appends content to the goal's living progress document in the knowledge graph and commits it.
    /// No-op when no knowledge graph is configured or the document does not exist.
    /// Failures are logged and swallowed — progress updates are best-effort and never block the pipeline.
    /// </summary>
    private async Task AppendToProgressDocumentAsync(string goalId, string content, CancellationToken ct)
    {
        if (_knowledgeGraph is null)
            return;

        var docId = $"progress-{goalId}";
        try
        {
            var doc = _knowledgeGraph.GetDocument(docId);
            if (doc is null)
                return;

            var newContent = doc.Content.TrimEnd() + "\n\n" + content;
            await _knowledgeGraph.UpdateDocumentAsync(docId, content: newContent, ct: ct);

            if (_configRepo is not null)
                await _knowledgeGraph.CommitToConfigRepoAsync(_configRepo.LocalPath, $"Update progress document: {docId}", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append to progress document for goal {GoalId}", goalId);
        }
    }

    /// <summary>
    /// Appends the worker narratives reported during the just-completed phase to the progress document.
    /// Narratives are filtered by the completing task's ID and ordered chronologically.
    /// </summary>
    private async Task AppendPhaseNarrativesAsync(GoalPipeline pipeline, TaskResult result, string workerRole, CancellationToken ct)
    {
        if (_knowledgeGraph is null || pipeline.Narratives.IsEmpty)
            return;

        var narratives = pipeline.Narratives
            .Where(n => n.TaskId == result.TaskId)
            .OrderBy(n => n.Timestamp)
            .ToList();

        if (narratives.Count == 0)
            return;

        var narrativeText = "";
        foreach (var entry in narratives)
            narrativeText += $"### {workerRole} (narrative)\n\n{entry.Content}\n\n";

        await AppendToProgressDocumentAsync(pipeline.GoalId, narrativeText.TrimEnd(), ct);
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

            // Mark the last Coding PhaseResult for the current iteration as failed FIRST —
            // before any budget checks and before any terminal exit — so the iteration summary
            // (built here on the retry path, or by FinalizeGoalAsync on the terminal path)
            // includes the failed Coding phase with the no-op reason visible in the dashboard.
            var codingEntry = pipeline.PhaseLog
                .LastOrDefault(e => e.Name == GoalPhase.Coding && e.Iteration == pipeline.Iteration);
            if (codingEntry is not null)
            {
                codingEntry.Result = PhaseOutcome.Fail;
                codingEntry.CompletedAt = DateTime.UtcNow;
                codingEntry.WorkerOutput = "Coder produced no file changes (no-op)";
            }

            // If the iteration budget is exhausted there is no retry possible: fail the goal
            // directly. The Coding phase was already marked failed above, so FinalizeGoalAsync's
            // terminal summary includes it. Do NOT build/add/persist a snapshot here — that would
            // create a duplicate summary when FinalizeGoalAsync runs.
            if (pipeline.IterationBudget.IsExhausted)
            {
                pipeline.StateMachine.Fail();
                pipeline.AdvanceTo(GoalPhase.Failed);
                await _lifecycleService.MarkGoalFailedAsync(pipeline, "Coder produced no file changes after max iterations (no-op)", ct);
                return;
            }

            // Snapshot the ending (no-op) iteration BEFORE IterationBudget.TryConsume() —
            // BuildIterationSummary filters PhaseLog by pipeline.Iteration, so calling it after
            // TryConsume would snapshot the WRONG (new) iteration. Same pattern as HandleNewIterationAsync.
            var iterationSummary = PipelineHelpers.BuildIterationSummary(pipeline);
            pipeline.CompletedIterationSummaries.Add(iterationSummary);

            // Persist the iteration summary to the goal source so the dashboard can read it.
            var updateMeta = new GoalUpdateMetadata { IterationSummary = iterationSummary };
            await _goalManager.UpdateGoalStatusAsync(pipeline.GoalId, GoalStatus.InProgress, updateMeta, ct);

            // Advance the iteration budget — guaranteed to succeed since IsExhausted was checked above.
            pipeline.IterationBudget.TryConsume();

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

            // PhaseLog: append a new entry for the retry Coding phase of the new iteration.
            // Without this, CurrentPhaseEntry would still point at the failed no-op Coding entry
            // and the retry worker's output/verdict would overwrite the no-op history.
            var retryEntry = PhaseResult.Create(GoalPhase.Coding, pipeline.Iteration, 1);
            retryEntry.WorkerPrompt = retryPrompt;
            retryEntry.BrainPrompt = PipelineHelpers.GetLastCraftPromptFromConversation(pipeline);
            pipeline.PhaseLog.Add(retryEntry);

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
        {
            var changedFiles = result.GitStatus.ChangedFiles;
            if (changedFiles.Count > 0)
            {
                var remaining = result.GitStatus.FilesChanged - changedFiles.Count;
                var pathList = string.Join(", ", changedFiles.Select(SanitizeLogPath));
                var moreMarker = remaining > 0 ? $" (+{remaining} more)" : "";
                _logger.LogWarning(
                    "Task {TaskId} had {Files} file changes but push failed. Changed files: {ChangedFiles}{More}",
                    result.TaskId, result.GitStatus.FilesChanged, pathList, moreMarker);
            }
            else
            {
                _logger.LogWarning(
                    "Task {TaskId} had {Files} file changes but push failed",
                    result.TaskId, result.GitStatus.FilesChanged);
            }
        }

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

        // Append worker narratives for the completed phase to the living progress document.
        await AppendPhaseNarrativesAsync(pipeline, result, workerRole, ct);

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
                await AppendToProgressDocumentAsync(pipeline.GoalId,
                    "### Brain Summary (Final)\n\nGoal completed successfully.", ct);
                await _lifecycleService.MarkGoalCompletedAsync(pipeline, ct);
                break;
        }
    }

    /// <summary>
    /// Fails the goal because an iteration could not be planned. Synchronizes the state machine
    /// with the terminal phase (<see cref="GoalLifecycleService.MarkGoalFailedAsync"/> advances
    /// the pipeline phase itself, so this must not advance it beforehand).
    /// </summary>
    /// <param name="pipeline">The pipeline whose planning failed.</param>
    /// <param name="reason">Why planning failed.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task FailPlanningAsync(GoalPipeline pipeline, string reason, CancellationToken ct)
    {
        pipeline.StateMachine.Fail();
        await _lifecycleService.MarkGoalFailedAsync(pipeline, reason, ct);
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

        // NOTE: the pipeline phase is deliberately NOT advanced here. The new iteration's phase
        // is only known after planning succeeds and is set from newPlan.Phases[0] below. Advancing
        // to an assumed Coding phase would make planning observe a phase the Brain never chose,
        // and would leave that wrong assumption behind if the caller cancels mid-planning.

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

        // Re-plan the iteration with failure context — a planning failure fails the goal
        if (_brain is null)
        {
            await FailPlanningAsync(pipeline, "No brain available for re-planning", ct);
            return;
        }

        PlanResult planResult;
        try
        {
            planResult = await _resolvePlan(pipeline, null, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Cancelled by the planning call itself (e.g. Brain-side timeout), not by our caller —
            // fail the goal gracefully rather than leaving it half-started.
            _logger.LogWarning("Re-planning was cancelled for {GoalId}", pipeline.GoalId);
            await FailPlanningAsync(pipeline, "Planning failed: planning was cancelled", CancellationToken.None);
            return;
        }
        catch (OperationCanceledException)
        {
            // The caller's token was cancelled — service shutdown. Propagate so the goal
            // is NOT marked Failed (no spurious failure on shutdown).
            throw;
        }
        catch (Exception ex)
        {
            // Never fall back to a default plan — a throw fails the goal with the reason.
            _logger.LogError(ex, "Re-planning threw for {GoalId}", pipeline.GoalId);
            await FailPlanningAsync(pipeline, $"Planning failed: {ex.Message}", ct);
            return;
        }

        if (planResult.IsFailed)
        {
            _logger.LogWarning("Failed to re-plan iteration for {GoalId}: {Reason}",
                pipeline.GoalId, planResult.FailureReason);
            await FailPlanningAsync(pipeline, planResult.FailureReason!, ct);
            return;
        }

        var newPlan = planResult.Plan!;

        pipeline.SetPlan(newPlan);
        pipeline.StateMachine.StartIteration(newPlan.Phases);

        // Honour the plan: the new iteration starts with whatever phase the Brain planned first,
        // not an assumed Coding phase.
        var firstPhase = newPlan.Phases[0];
        pipeline.AdvanceTo(firstPhase);

        // Append a Brain summary of the completed iteration and the new plan to the progress document.
        var summaryAndPlan =
            $"### Brain Summary (Iteration {pipeline.Iteration - 1})\n\n" +
            $"Iteration resulted in: {verdict}. Proceeding to iteration {pipeline.Iteration}.\n\n" +
            PipelineProgressFormatting.BuildPlanSection(pipeline.Iteration, newPlan);
        await AppendToProgressDocumentAsync(pipeline.GoalId, summaryAndPlan, ct);

        _logger.LogInformation(
            "New iteration {Iteration} for goal {GoalId}: {Phases}",
            pipeline.Iteration, pipeline.GoalId, string.Join(" → ", newPlan.Phases));

        // Build context for the coder from the previous iteration's phase output
        var prevIteration = pipeline.Iteration - 1;
        var relevantPhase = isReviewRelated ? GoalPhase.Review : GoalPhase.Testing;
        var feedbackKindHeader = isReviewRelated
            ? $"Reviewer feedback from iteration {prevIteration}:"
            : $"Test failures from iteration {prevIteration}:";

        var context = feedbackKindHeader;

        var previousPhaseEntry = pipeline.PhaseLog
            .LastOrDefault(e => e.Iteration == prevIteration && e.Name == relevantPhase);
        var output = previousPhaseEntry?.WorkerOutput;

        if (!string.IsNullOrWhiteSpace(output))
        {
            const int maxOutputChars = 3000;
            var truncated = output.Length > maxOutputChars
                ? output[..maxOutputChars] + "..."
                : output;
            context += $"\n{truncated}";
        }
        else
        {
            context += "\n(No detailed output available — check iteration summary.)";
        }

        if (reviewIssues is { Count: > 0 })
        {
            var allIssues = string.Join("\n", reviewIssues);
            context += $"\n\nAccumulated issues from all review rounds (fix ALL of these):\n{allIssues}";
        }

        var fixPrompt = _brain is not null
            ? await _resolvePrompt(pipeline, firstPhase, context, ct)
            : $"Fix issues for: {pipeline.Description}. {context}";

        // PhaseLog: append a new entry for the first planned phase in the new iteration
        pipeline.PhaseLog.Add(PhaseResult.Create(firstPhase, pipeline.Iteration, 1));
        if (pipeline.CurrentPhaseEntry is { } newIterEntry)
        {
            newIterEntry.WorkerPrompt = fixPrompt;
            newIterEntry.BrainPrompt = PipelineHelpers.GetLastCraftPromptFromConversation(pipeline);
            // Capture planning prompt/response from conversation onto the first entry of the new iteration
            var (planningPrompt, planningResponse) = PipelineHelpers.GetPlanningPromptsFromConversation(pipeline);
            newIterEntry.PlanningPrompt = planningPrompt;
            newIterEntry.PlanningResponse = planningResponse;
        }

        await _dispatchToRole(pipeline, firstPhase.ToWorkerRole(), fixPrompt, ct);
    }

    public async Task HandleMergeFailureAsync(GoalPipeline pipeline, string errorMessage, CancellationToken ct)
    {
        // Mark the current Merging PhaseResult as failed FIRST — before any budget checks and
        // before any terminal exit — so the iteration summary (built here on the retry path, or
        // by FinalizeGoalAsync on the terminal paths) includes the failed Merging phase with the
        // merge error visible in the dashboard.
        var mergingEntry = pipeline.PhaseLog
            .LastOrDefault(e => e.Name == GoalPhase.Merging && e.Iteration == pipeline.Iteration);
        if (mergingEntry is not null)
        {
            mergingEntry.Result = PhaseOutcome.Fail;
            mergingEntry.CompletedAt = DateTime.UtcNow;
            mergingEntry.WorkerOutput = errorMessage;
        }

        // State machine already transitioned to Coding (NewIteration) before this is called.
        // Check retry/iteration limits.
        if (!pipeline.ReviewRetryBudget.TryConsume())
        {
            // Terminal path: the Merging phase was marked failed above. Do NOT build/add/persist
            // an iteration summary here — FinalizeGoalAsync builds and persists the terminal
            // summary, so adding one here would create a duplicate.
            pipeline.StateMachine.Fail();
            pipeline.AdvanceTo(GoalPhase.Failed);
            await AppendToProgressDocumentAsync(pipeline.GoalId,
                $"### Brain Summary (Final)\n\nMerge failed after max retries: {errorMessage}", ct);
            await _lifecycleService.MarkGoalFailedAsync(pipeline, $"Merge failed after max retries: {errorMessage}", ct);
            return;
        }

        _logger.LogInformation(
            "Merge conflict for goal {GoalId} — sending back to Coder for rebase (retry {Retry}/{Max})",
            pipeline.GoalId, pipeline.ReviewRetryBudget.Used, pipeline.ReviewRetryBudget.Allowed);

        // If the iteration budget is exhausted there is no retry possible: fail the goal directly.
        // The Merging phase was already marked failed above, so FinalizeGoalAsync's terminal
        // summary includes it. Do NOT build/add/persist a snapshot here — that would create a
        // duplicate summary when FinalizeGoalAsync runs.
        if (pipeline.IterationBudget.IsExhausted)
        {
            pipeline.StateMachine.Fail();
            pipeline.AdvanceTo(GoalPhase.Failed);
            await AppendToProgressDocumentAsync(pipeline.GoalId,
                "### Brain Summary (Final)\n\nExceeded max iterations during merge conflict resolution.", ct);
            await _lifecycleService.MarkGoalFailedAsync(pipeline, "Exceeded max iterations during merge conflict resolution", ct);
            return;
        }

        // Snapshot the ending (failed-merge) iteration BEFORE IterationBudget.TryConsume() —
        // BuildIterationSummary filters PhaseLog by pipeline.Iteration, so calling it after
        // TryConsume would snapshot the WRONG (new) iteration. Same pattern as HandleNewIterationAsync.
        var iterationSummary = PipelineHelpers.BuildIterationSummary(pipeline);
        pipeline.CompletedIterationSummaries.Add(iterationSummary);

        // Persist the iteration summary to the goal source so the dashboard can read it.
        var updateMeta = new GoalUpdateMetadata { IterationSummary = iterationSummary };
        await _goalManager.UpdateGoalStatusAsync(pipeline.GoalId, GoalStatus.InProgress, updateMeta, ct);

        // Advance the iteration budget — guaranteed to succeed since IsExhausted was checked above.
        pipeline.IterationBudget.TryConsume();

        // NOTE: the pipeline phase is deliberately NOT advanced here — see HandleNewIterationAsync.
        // It is set from newPlan.Phases[0] once planning succeeds.

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

        // Re-plan with full pipeline so the rebase goes through review and testing —
        // a planning failure fails the goal rather than substituting a default plan.
        if (_brain is null)
        {
            await FailPlanningAsync(pipeline, "No brain available for re-planning", ct);
            return;
        }

        PlanResult mergePlanResult;
        try
        {
            mergePlanResult = await _resolvePlan(pipeline, null, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Merge-failure re-planning was cancelled for {GoalId}", pipeline.GoalId);
            await FailPlanningAsync(pipeline, "Planning failed: planning was cancelled", CancellationToken.None);
            return;
        }
        catch (OperationCanceledException)
        {
            // The caller's token was cancelled — service shutdown. Propagate so the goal
            // is NOT marked Failed (no spurious failure on shutdown).
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Merge-failure re-planning threw for {GoalId}", pipeline.GoalId);
            await FailPlanningAsync(pipeline, $"Planning failed: {ex.Message}", ct);
            return;
        }

        if (mergePlanResult.IsFailed)
        {
            _logger.LogWarning("Failed to re-plan merge-failure iteration for {GoalId}: {Reason}",
                pipeline.GoalId, mergePlanResult.FailureReason);
            await FailPlanningAsync(pipeline, mergePlanResult.FailureReason!, ct);
            return;
        }

        var newPlan = mergePlanResult.Plan!;

        pipeline.SetPlan(newPlan);
        pipeline.StateMachine.StartIteration(newPlan.Phases);

        // Honour the plan: start with the first planned phase rather than assuming Coding.
        var firstPhase = newPlan.Phases[0];
        pipeline.AdvanceTo(firstPhase);

        // Append a Brain summary of the failed-merge iteration and the new plan to the progress document.
        var mergeSummaryAndPlan =
            $"### Brain Summary (Iteration {pipeline.Iteration - 1})\n\n" +
            $"Merge conflict encountered. Retrying with rebase. {errorMessage}\n\n" +
            PipelineProgressFormatting.BuildPlanSection(pipeline.Iteration, newPlan);
        await AppendToProgressDocumentAsync(pipeline.GoalId, mergeSummaryAndPlan, ct);

        var fixPrompt = _brain is not null
            ? await _resolvePrompt(pipeline, firstPhase, rebaseContext, ct)
            : rebaseContext;

        // PhaseLog: append a new entry for the first planned phase of the retry iteration.
        // Without this, CurrentPhaseEntry would still point at the prior Merging entry and the
        // retry worker's output/verdict would overwrite the merge history.
        pipeline.PhaseLog.Add(PhaseResult.Create(firstPhase, pipeline.Iteration, 1));
        if (pipeline.CurrentPhaseEntry is { } firstPhaseEntry)
        {
            firstPhaseEntry.WorkerPrompt = fixPrompt;
            firstPhaseEntry.BrainPrompt = PipelineHelpers.GetLastCraftPromptFromConversation(pipeline);
            // Capture planning prompt/response from conversation onto the first entry of the new iteration
            var (planningPrompt, planningResponse) = PipelineHelpers.GetPlanningPromptsFromConversation(pipeline);
            firstPhaseEntry.PlanningPrompt = planningPrompt;
            firstPhaseEntry.PlanningResponse = planningResponse;
        }

        await _dispatchToRole(pipeline, firstPhase.ToWorkerRole(), fixPrompt, ct);
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

                    // Advance past the failed Improve phase (non-blocking in state machine)
                    var skipResult = pipeline.StateMachine.Transition(PhaseInput.Failed);
                    pipeline.AdvanceTo(skipResult.NextPhase);
                    if (skipResult.Effect == TransitionEffect.Continue)
                        await DispatchPhaseAsync(pipeline, skipResult.NextPhase, null, ct);
                    else if (skipResult.Effect == TransitionEffect.Completed)
                    {
                        await AppendToProgressDocumentAsync(pipeline.GoalId,
                            "### Brain Summary (Final)\n\nGoal completed successfully.", ct);
                        await _lifecycleService.MarkGoalCompletedAsync(pipeline, ct);
                    }
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

        if (_knowledgeGraph is not null)
        {
            var progressDoc = _knowledgeGraph.GetDocument($"progress-{pipeline.GoalId}");
            if (progressDoc is not null && !string.IsNullOrWhiteSpace(progressDoc.Content))
            {
                improveContext +=
                    "\n\n## Iteration Progress Document\n" +
                    "Use the qualitative context below to identify recurring patterns and extract actionable " +
                    "guidance rules for agents.md. Do NOT copy it as a changelog or iteration history.\n\n" +
                    progressDoc.Content;
            }
        }

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
            string? brainSummary = null;
            if (_brain is not null)
            {
                try
                {
                    brainSummary = await _brain.SummarizeAndMergeAsync(pipeline, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to summarize goal '{GoalId}' — deleting goal session without merge", pipeline.GoalId);
                    await _brain.DeleteGoalSessionAsync(pipeline.GoalId, ct);
                }
            }

            // Append the final Brain summary to the living progress document. This is the REAL normal
            // completion path (Merging phase → PerformMergeAsync), not the DriveNextPhaseAsync
            // TransitionEffect.Completed case which only fires for worker-driven completions.
            var finalSummary = !string.IsNullOrWhiteSpace(brainSummary)
                ? brainSummary
                : "Goal completed successfully.";
            await AppendToProgressDocumentAsync(pipeline.GoalId,
                $"### Brain Summary (Final)\n\n{finalSummary}", ct);

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
