using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;

namespace CopilotHive.Dashboard;

/// <summary>
/// Contains the pure view-building logic extracted from <see cref="DashboardStateService"/>.
/// All methods are static and receive their dependencies (goal, pipeline, config) as parameters.
/// </summary>
internal static class GoalDetailViewBuilder
{
    // ── Public entry point ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds a rich <see cref="GoalDetailInfo"/> for the given goal, including
    /// per-iteration phase info derived from both persisted summaries and a live pipeline.
    /// </summary>
    /// <param name="goal">
    /// The goal from the snapshot (lightweight — may lack IterationSummaries).
    /// Used as a fallback when <paramref name="fullGoalWithSummaries"/> is null.
    /// </param>
    /// <param name="goalId">The goal identifier.</param>
    /// <param name="pipeline">The active pipeline, or null if the goal is completed.</param>
    /// <param name="fullGoalWithSummaries">
    /// The full goal with IterationSummaries loaded from the store, or null to use <paramref name="goal"/> directly.
    /// </param>
    /// <param name="config">Hive configuration, or null.</param>
    /// <returns>A fully-populated <see cref="GoalDetailInfo"/>, or null if the goal is not found.</returns>
    public static GoalDetailInfo? Build(
        Goal goal,
        string goalId,
        GoalPipeline? pipeline,
        Goal? fullGoalWithSummaries,
        HiveConfigFile? config)
    {
        // Use the full goal with summaries if provided (store path), otherwise use lightweight goal.
        // The authoritative goal is used for ALL view-model fields (not just Iterations).
        var effectiveGoal = fullGoalWithSummaries ?? goal;

        var iterations = BuildIterationTimeline(effectiveGoal, goalId, pipeline);

        // Derive effective status from pipeline phase
        var effectiveStatus = pipeline?.Phase switch
        {
            GoalPhase.Done => GoalStatus.Completed,
            GoalPhase.Failed => GoalStatus.Failed,
            not null => GoalStatus.InProgress,
            _ => effectiveGoal.Status,
        };

        return new GoalDetailInfo
        {
            GoalId = goalId,
            Description = effectiveGoal.Description,
            Status = effectiveStatus,
            Priority = effectiveGoal.Priority,
            Scope = effectiveGoal.Scope,
            CurrentIteration = pipeline?.Iteration ?? 0,
            CurrentPhase = pipeline?.Phase.ToDisplayName() ?? "",
            CreatedAt = pipeline?.CreatedAt ?? effectiveGoal.CreatedAt,
            CompletedAt = pipeline?.CompletedAt ?? effectiveGoal.CompletedAt,
            ActiveTaskId = pipeline?.ActiveTaskId,
            CoderBranch = pipeline?.CoderBranch,
            Notes = effectiveGoal.Notes,
            DependsOn = effectiveGoal.DependsOn,
            Iterations = iterations,
            Conversation = pipeline?.Conversation.ToList() ?? [],
            MergeCommitHash = pipeline?.MergeCommitHash ?? effectiveGoal.MergeCommitHash,
            RepositoryUrl = ResolveRepositoryUrl(effectiveGoal, config),
            RepositoryNames = effectiveGoal.RepositoryNames,
        };
    }

    // ── IterationTimeline ──────────────────────────────────────────────────────

    /// <summary>
    /// Builds the list of <see cref="IterationViewInfo"/> entries for a goal,
    /// covering both summarised (completed) iterations and the live pipeline iteration.
    /// </summary>
    public static List<IterationViewInfo> BuildIterationTimeline(
        Goal goal, string goalId, GoalPipeline? pipeline)
    {
        var iterations = new List<IterationViewInfo>();

        // Build views for completed iterations from IterationSummaries.
        // Merge persisted summaries (from goal source) with in-memory summaries (from pipeline).
        var allSummaries = new List<IterationSummary>(goal.IterationSummaries);
        if (pipeline is not null)
        {
            foreach (var inMemory in pipeline.CompletedIterationSummaries)
            {
                if (!allSummaries.Any(s => s.Iteration == inMemory.Iteration))
                    allSummaries.Add(inMemory);
            }
        }
        allSummaries.Sort((a, b) => a.Iteration.CompareTo(b.Iteration));

        foreach (var summary in allSummaries)
        {
            var summaryConversation = pipeline?.Conversation ?? Enumerable.Empty<ConversationEntry>();
            var (summaryPlanningPrompt, summaryPlanningResponse) = ExtractPlanningPrompts(summaryConversation, summary.Iteration);

            var phases = BuildPhasesFromSummary(goalId, summary, pipeline);

            iterations.Add(new IterationViewInfo
            {
                Number = summary.Iteration,
                Phases = phases,
                IsCurrent = false,
                PlanningBrainPrompt = summaryPlanningPrompt,
                PlanningBrainResponse = summaryPlanningResponse,
            });
        }

        // Build view for the current/unsummarized iteration from pipeline state.
        if (pipeline is not null && !iterations.Any(i => i.Number == pipeline.Iteration))
        {
            var currentIter = pipeline.Iteration;
            var isCurrent = pipeline.Phase is not GoalPhase.Done and not GoalPhase.Failed;
            var (planningBrainPrompt, planningBrainResponse) = ExtractPlanningPrompts(pipeline.Conversation, currentIter);

            var currentPhases = BuildPhasesFromPipeline(goalId, pipeline, currentIter);

            iterations.Add(new IterationViewInfo
            {
                Number = currentIter,
                Phases = currentPhases,
                IsCurrent = isCurrent,
                PlanReason = pipeline.Plan?.Reason,
                PlanningBrainPrompt = planningBrainPrompt,
                PlanningBrainResponse = planningBrainResponse,
            });
        }

        return iterations;
    }

    // ── BuildPhasesFromSummary ──────────────────────────────────────────────────

    /// <summary>
    /// Builds the list of <see cref="PhaseViewInfo"/> entries for a summarised (completed)
    /// iteration, populating WorkerOutput from persisted PhaseResult data and
    /// supplementing with live pipeline data where needed.
    /// </summary>
    public static List<PhaseViewInfo> BuildPhasesFromSummary(
        string goalId, IterationSummary summary, GoalPipeline? pipeline)
    {
        var conversation = pipeline?.Conversation ?? Enumerable.Empty<ConversationEntry>();
        var craftPrompts = ExtractCraftPrompts(conversation, summary.Iteration);

        // Group persisted clarifications by phase name for this iteration.
        var clarificationsByPhase = summary.Clarifications
            .GroupBy(c => c.Phase, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(c => new ClarificationEntry(
                    Timestamp: c.Timestamp,
                    GoalId: goalId,
                    Iteration: summary.Iteration,
                    Phase: c.Phase,
                    WorkerRole: c.WorkerRole,
                    Question: c.Question,
                    Answer: c.Answer,
                    AnsweredBy: c.AnsweredBy)
                {
                    Occurrence = c.Occurrence,
                }).ToList(),
                StringComparer.OrdinalIgnoreCase);

        clarificationsByPhase.TryGetValue("Planning", out var planningClarifications);

        var phases = new List<PhaseViewInfo>
        {
            new PhaseViewInfo
            {
                Name = "Planning",
                RoleName = "brain",
                Status = "completed",
                Clarifications = planningClarifications ?? [],
                ProgressReports = pipeline?.ProgressReports
                    .Where(p => p.Iteration == summary.Iteration && p.Phase == "Planning")
                    .OrderBy(p => p.Timestamp)
                    .ToList() ?? [],
            },
        };

        foreach (var pr in summary.Phases)
        {
            var roleName = PhaseNameToRoleName(pr.Name);

            // Count total occurrences per phase in this summary for last-occurrence detection.
            var totalOccurrencesForPhase = summary.Phases.Count(p => p.Name == pr.Name);
            // Current occurrence: use PhaseResult.Occurrence if set, else default to 1.
            var occurrence = pr.Occurrence ?? 1;
            var isLastOccurrence = occurrence >= totalOccurrencesForPhase;

            // Prefer PhaseResult.WorkerOutput (persisted) over live pipeline PhaseOutputs.
            // This ensures completed goals (no pipeline) still display output correctly.
            string? workerOutput = pr.WorkerOutput;
            if (string.IsNullOrEmpty(workerOutput) && !string.IsNullOrEmpty(roleName))
            {
                // Use occurrence-specific key if Occurrence is set; otherwise fall back to latest.
                if (pr.Occurrence.HasValue)
                    pipeline?.PhaseOutputs.TryGetValue($"{roleName}-{summary.Iteration}-{pr.Occurrence.Value}", out workerOutput);
                // Only fall back to latest key for single-occurrence or last-occurrence phases
                if (string.IsNullOrEmpty(workerOutput) && isLastOccurrence)
                    pipeline?.PhaseOutputs.TryGetValue($"{roleName}-{summary.Iteration}", out workerOutput);
            }

            var isTestPhase = pr.Name == "Testing";
            var isReviewPhase = pr.Name == "Review";

            // Filter clarifications by occurrence using the OccurrenceFilter helper.
            List<ClarificationEntry>? phaseClarifications = null;
            if (clarificationsByPhase.TryGetValue(pr.Name, out var allPhaseClarifications))
            {
                phaseClarifications = OccurrenceFilter.FilterByOccurrence(allPhaseClarifications, occurrence);
            }

            craftPrompts.TryGetValue(roleName, out var phasePrompts);

            // Filter progress reports by occurrence using the OccurrenceFilter helper.
            var allPhaseProgress = pipeline?.ProgressReports
                .Where(p => p.Iteration == summary.Iteration && p.Phase == pr.Name)
                .OrderBy(p => p.Timestamp)
                .ToList() ?? [];
            var phaseProgress = OccurrenceFilter.FilterByOccurrence(allPhaseProgress, occurrence);

            phases.Add(BuildPhaseViewInfo(
                phaseIndex: phases.Count,
                phase: pr,
                workerOutput: workerOutput,
                clarifications: phaseClarifications,
                progress: phaseProgress,
                summary: summary,
                isTestPhase: isTestPhase,
                isReviewPhase: isReviewPhase,
                craftPrompts: phasePrompts,
                pipeline: pipeline,
                occurrence: occurrence,
                isLastOccurrence: isLastOccurrence));
        }

        return phases;
    }

    // ── BuildPhasesFromPipeline ────────────────────────────────────────────────

    /// <summary>
    /// Builds the list of <see cref="PhaseViewInfo"/> entries for the live pipeline's
    /// current iteration, including positional status computation for multi-round plans.
    /// </summary>
    public static List<PhaseViewInfo> BuildPhasesFromPipeline(string goalId, GoalPipeline pipeline, int currentIter)
    {
        // Determine the planning phase status.
        var planningStatus = pipeline.Phase == GoalPhase.Planning ? "active" : "completed";

        var craftPrompts = ExtractCraftPrompts(pipeline.Conversation, currentIter);

        // Collect all clarifications from this pipeline, grouped by phase name.
        var clarificationsByPhase = pipeline.Clarifications
            .Where(c => c.Iteration == currentIter)
            .GroupBy(c => c.Phase, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        clarificationsByPhase.TryGetValue("Planning", out var planningClarifications);

        var phases = new List<PhaseViewInfo>
        {
            new PhaseViewInfo
            {
                Name = "Planning",
                RoleName = "brain",
                Status = planningStatus,
                Clarifications = planningClarifications ?? [],
                ProgressReports = pipeline.ProgressReports
                    .Where(p => p.Iteration == currentIter && p.Phase == "Planning")
                    .OrderBy(p => p.Timestamp)
                    .ToList(),
            },
        };

        // CRITICAL: Only show Planning alone when actively in Planning phase.
        // Once past Planning, ALWAYS show worker phases (even if Plan is null).
        if (pipeline.Phase != GoalPhase.Planning)
        {
            var planPhases = pipeline.Plan?.Phases ?? [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging];
            var failedFound = false;
            var isCurrent = pipeline.Phase is not GoalPhase.Done and not GoalPhase.Failed;
            var completedCount = planPhases.Count - pipeline.StateMachine.RemainingPhases.Count - 1;
            if (pipeline.Phase == GoalPhase.Done)
                completedCount = planPhases.Count;

            var occurrenceCounters = new Dictionary<GoalPhase, int>();
            for (var i = 0; i < planPhases.Count; i++)
            {
                var phase = planPhases[i];
                occurrenceCounters[phase] = occurrenceCounters.GetValueOrDefault(phase) + 1;
                var occurrence = occurrenceCounters[phase];

                // Compute isLastOccurrence early — needed for fallback logic below.
                var lastPlanIndex = -1;
                for (var j = planPhases.Count - 1; j >= 0; j--)
                {
                    if (planPhases[j] == phase)
                    {
                        lastPlanIndex = j;
                        break;
                    }
                }
                var isLastOccurrence = (i == lastPlanIndex);

                string status;
                if (i < completedCount)
                    status = "completed";
                else if (i == completedCount && isCurrent)
                    status = pipeline.IsWaitingForClarification ? "waiting" : "active";
                else if (i == completedCount && pipeline.Phase == GoalPhase.Failed && !failedFound)
                    { status = "failed"; failedFound = true; }
                else if (pipeline.Phase == GoalPhase.Done)
                    status = "completed";
                else
                    status = "pending";

                var roleName = GetRoleNameSafe(phase);
                string? workerOutput = null;
                if (!string.IsNullOrEmpty(roleName))
                {
                    // Try per-occurrence key first
                    if (!pipeline.PhaseOutputs.TryGetValue($"{roleName}-{currentIter}-{occurrence}", out workerOutput))
                    {
                        // Only fall back to the backward-compatible latest key for the last
                        // occurrence of each phase type (or for single-occurrence phases).
                        // Non-last occurrences with missing per-occurrence keys get null
                        // instead of stale data from a later occurrence.
                        if (isLastOccurrence)
                            pipeline.PhaseOutputs.TryGetValue($"{roleName}-{currentIter}", out workerOutput);
                    }
                }

                var isTestPhase = phase == GoalPhase.Testing;
                var isReviewPhase = phase == GoalPhase.Review;

                // Filter clarifications by occurrence using the OccurrenceFilter helper.
                var phaseName = phase.ToString();
                List<ClarificationEntry>? phaseClarifications = null;
                if (clarificationsByPhase.TryGetValue(phaseName, out var allPhaseClarifications))
                {
                    phaseClarifications = OccurrenceFilter.FilterByOccurrence(allPhaseClarifications, occurrence);
                }

                craftPrompts.TryGetValue(roleName, out var phasePrompts);

                // Filter progress reports by occurrence using the OccurrenceFilter helper.
                var allPhaseProgress = pipeline.ProgressReports
                    .Where(p => p.Iteration == currentIter && p.Phase == phaseName)
                    .OrderBy(p => p.Timestamp)
                    .ToList();
                var phaseProgress = OccurrenceFilter.FilterByOccurrence(allPhaseProgress, occurrence);

                phases.Add(BuildPhaseViewInfo(
                    phaseIndex: phases.Count,
                    phase: phase,
                    workerOutput: workerOutput,
                    clarifications: phaseClarifications ?? [],
                    progress: phaseProgress,
                    pipeline: pipeline,
                    isLastOccurrence: isLastOccurrence,
                    occurrence: occurrence,
                    status: status,
                    isTestPhase: isTestPhase,
                    isReviewPhase: isReviewPhase,
                    craftPrompts: phasePrompts,
                    currentIter: currentIter));
            }
        }

        return phases;
    }

    // ── BuildPhaseViewInfo overloads ──────────────────────────────────────────

    /// <summary>
    /// Overload for building a <see cref="PhaseViewInfo"/> from a persisted
    /// <see cref="PhaseResult"/> (summarised-iteration path).
    /// </summary>
    public static PhaseViewInfo BuildPhaseViewInfo(
        int phaseIndex,
        PhaseResult phase,
        string? workerOutput,
        List<ClarificationEntry>? clarifications,
        List<ProgressEntry> progress,
        IterationSummary? summary,
        bool isTestPhase,
        bool isReviewPhase,
        (string? BrainPrompt, string? WorkerPrompt)? craftPrompts,
        GoalPipeline? pipeline,
        int occurrence = 1,
        bool isLastOccurrence = true)
    {
        return new PhaseViewInfo
        {
            Name = phase.Name,
            RoleName = PhaseNameToRoleName(phase.Name),
            Status = phase.Result switch { "pass" => "completed", "fail" => "failed", "skip" => "skipped", _ => "completed" },
            DurationSeconds = phase.DurationSeconds > 0 ? phase.DurationSeconds : null,
            Occurrence = occurrence,
            WorkerOutput = workerOutput,
            TotalTests = isTestPhase && isLastOccurrence ? (summary?.TestCounts?.Total ?? 0) : 0,
            PassedTests = isTestPhase && isLastOccurrence ? (summary?.TestCounts?.Passed ?? 0) : 0,
            FailedTests = isTestPhase && isLastOccurrence ? (summary?.TestCounts?.Failed ?? 0) : 0,
            BuildSuccess = isTestPhase && isLastOccurrence && (summary?.BuildSuccess ?? false),
            ReviewVerdict = isReviewPhase && isLastOccurrence ? summary?.ReviewVerdict : null,
            BrainPrompt = craftPrompts?.BrainPrompt,
            WorkerPrompt = craftPrompts?.WorkerPrompt,
            Clarifications = clarifications ?? [],
            ProgressReports = progress,
        };
    }

    /// <summary>
    /// Overload for building a <see cref="PhaseViewInfo"/> from a live <see cref="GoalPhase"/>
    /// (live-pipeline path). Computes metrics and positional status for multi-round plans.
    /// </summary>
    public static PhaseViewInfo BuildPhaseViewInfo(
        int phaseIndex,
        GoalPhase phase,
        string? workerOutput,
        List<ClarificationEntry>? clarifications,
        List<ProgressEntry> progress,
        GoalPipeline pipeline,
        bool isLastOccurrence,
        int occurrence = 1,
        string status = "pending",
        bool isTestPhase = false,
        bool isReviewPhase = false,
        (string? BrainPrompt, string? WorkerPrompt)? craftPrompts = null,
        int currentIter = 1)
    {
        var metrics = pipeline.Metrics;
        var hasMetrics = status is "completed" or "active" or "failed" or "waiting";

        return new PhaseViewInfo
        {
            Name = phase.ToDisplayName(),
            RoleName = GetRoleNameSafe(phase),
            Status = status,
            Occurrence = occurrence,
            WorkerOutput = workerOutput,
            DurationSeconds = metrics.PhaseDurations.TryGetValue($"{phase}-{occurrence}", out var occDur)
                ? occDur.TotalSeconds
                : (metrics.PhaseDurations.TryGetValue(phase.ToString(), out var dur) ? dur.TotalSeconds : null),
            TotalTests = hasMetrics && isTestPhase && isLastOccurrence ? metrics.TotalTests : 0,
            PassedTests = hasMetrics && isTestPhase && isLastOccurrence ? metrics.PassedTests : 0,
            FailedTests = hasMetrics && isTestPhase && isLastOccurrence ? metrics.FailedTests : 0,
            CoveragePercent = hasMetrics && isTestPhase && isLastOccurrence ? metrics.CoveragePercent : 0,
            BuildSuccess = hasMetrics && isTestPhase && isLastOccurrence && metrics.BuildSuccess,
            ReviewVerdict = hasMetrics && isReviewPhase ? metrics.ReviewVerdict?.ToString() : null,
            ReviewIssuesFound = hasMetrics && isReviewPhase ? metrics.ReviewIssuesFound : 0,
            Issues = hasMetrics && isReviewPhase ? metrics.ReviewIssues.ToList() :
                     hasMetrics && isTestPhase && isLastOccurrence ? metrics.Issues.ToList() : [],
            Verdict = hasMetrics && isTestPhase && isLastOccurrence ? metrics.Verdict?.ToString() : null,
            ProgressReports = progress,
            BrainPrompt = craftPrompts?.BrainPrompt,
            WorkerPrompt = craftPrompts?.WorkerPrompt,
            Clarifications = clarifications ?? [],
        };
    }

    // ── Extract helpers (already static) ─────────────────────────────────────

    /// <summary>
    /// Extracts the planning Brain prompt and response for a given iteration from the conversation log.
    /// </summary>
    /// <param name="conversation">The pipeline conversation entries to search.</param>
    /// <param name="iteration">The iteration number to filter by.</param>
    /// <returns>A tuple of (userPrompt, assistantResponse), each of which may be null if not found.</returns>
    public static (string? UserPrompt, string? AssistantResponse) ExtractPlanningPrompts(
        IEnumerable<ConversationEntry> conversation, int iteration)
    {
        var planningEntries = conversation
            .Where(e => e.Iteration == iteration && e.Purpose == "planning")
            .ToList();
        var userPrompt = planningEntries.LastOrDefault(e => e.Role == "user")?.Content;
        var assistantResponse = planningEntries.LastOrDefault(e => e.Role == "assistant")?.Content;
        return (userPrompt, assistantResponse);
    }

    /// <summary>
    /// Walks the conversation log and associates craft-prompt pairs with the worker role
    /// that follows them. Returns a dictionary keyed by role name (e.g. "coder", "tester").
    /// </summary>
    /// <param name="conversation">The pipeline conversation entries to search.</param>
    /// <param name="iteration">The iteration number to filter by.</param>
    /// <returns>A dictionary from role name to its associated (BrainPrompt, WorkerPrompt) pair.</returns>
    public static Dictionary<string, (string? BrainPrompt, string? WorkerPrompt)> ExtractCraftPrompts(
        IEnumerable<ConversationEntry> conversation, int iteration)
    {
        var result = new Dictionary<string, (string? BrainPrompt, string? WorkerPrompt)>(StringComparer.OrdinalIgnoreCase);

        // Filter to entries for this iteration, excluding planning entries
        var entries = conversation
            .Where(e => e.Iteration == iteration && e.Purpose != "planning")
            .ToList();

        // Walk entries; collect pending craft-prompt pair and associate with the next worker-output role
        string? pendingBrainPrompt = null;
        string? pendingWorkerPrompt = null;

        foreach (var entry in entries)
        {
            if (entry.Purpose == "craft-prompt")
            {
                if (entry.Role == "user")
                {
                    // Only capture the first craft-prompt request in each phase block.
                    // Mid-task AskBrainAsync follow-ups also use Purpose=="craft-prompt",
                    // so we must not overwrite the initial dispatch prompt.
                    if (pendingBrainPrompt is null)
                    {
                        pendingBrainPrompt = entry.Content;
                        pendingWorkerPrompt = null;
                    }
                }
                else if (entry.Role == "assistant")
                {
                    // Only record the response for the first pair; ignore follow-up assistant turns.
                    if (pendingBrainPrompt is not null && pendingWorkerPrompt is null)
                        pendingWorkerPrompt = entry.Content;
                }
            }
            else if (entry.Purpose == "worker-output")
            {
                // Associate the accumulated craft-prompt pair with this worker role
                if (!string.IsNullOrEmpty(entry.Role))
                    result[entry.Role] = (pendingBrainPrompt, pendingWorkerPrompt);
                // Reset pending state for the next worker
                pendingBrainPrompt = null;
                pendingWorkerPrompt = null;
            }
        }

        return result;
    }

    // ── Repository URL resolution ─────────────────────────────────────────────

    /// <summary>
    /// Resolves the URL of the first repository associated with a goal,
    /// stripping any <c>.git</c> suffix. Returns <c>null</c> if the goal has no
    /// repository names or the repository is not found in the configuration.
    /// </summary>
    /// <param name="goal">The goal whose primary repository URL is needed.</param>
    /// <param name="config">Hive configuration containing repository definitions, or null.</param>
    /// <returns>The repository base URL, or <c>null</c> if unavailable.</returns>
    public static string? ResolveRepositoryUrl(Goal goal, HiveConfigFile? config)
    {
        if (config is null || goal.RepositoryNames.Count == 0)
            return null;

        var firstName = goal.RepositoryNames[0];
        var repoConfig = config.Repositories.FirstOrDefault(r =>
            string.Equals(r.Name, firstName, StringComparison.OrdinalIgnoreCase));

        if (repoConfig is null)
            return null;

        var url = repoConfig.Url;
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];

        return url;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static string PhaseNameToRoleName(string phaseName) => phaseName switch
    {
        "Coding" => "coder",
        "Testing" => "tester",
        "Review" => "reviewer",
        "Doc Writing" => "docwriter",
        "Improvement" => "improver",
        "Improve" => "improver",
        _ => "",
    };

    private static string GetRoleNameSafe(GoalPhase phase) => phase switch
    {
        GoalPhase.Coding => "coder",
        GoalPhase.Testing => "tester",
        GoalPhase.Review => "reviewer",
        GoalPhase.DocWriting => "docwriter",
        GoalPhase.Improve => "improver",
        _ => "",
    };
}
