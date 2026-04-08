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
            // Read planning prompt/response from the first PhaseResult in the summary
            var firstSummaryPhase = summary.Phases.FirstOrDefault();

            var phases = BuildPhasesFromSummary(goalId, summary, pipeline);

            iterations.Add(new IterationViewInfo
            {
                Number = summary.Iteration,
                Phases = phases,
                IsCurrent = false,
                PlanningBrainPrompt = firstSummaryPhase?.PlanningPrompt,
                PlanningBrainResponse = firstSummaryPhase?.PlanningResponse,
            });
        }

        // Build view for the current/unsummarized iteration from pipeline state.
        if (pipeline is not null && !iterations.Any(i => i.Number == pipeline.Iteration))
        {
            var currentIter = pipeline.Iteration;
            var isCurrent = pipeline.Phase is not GoalPhase.Done and not GoalPhase.Failed;

            // Read planning prompt/response from the first PhaseLog entry for this iteration
            var firstLogEntry = pipeline.PhaseLog
                .FirstOrDefault(e => e.Iteration == currentIter);

            var currentPhases = BuildPhasesFromPipeline(goalId, pipeline, currentIter);

            iterations.Add(new IterationViewInfo
            {
                Number = currentIter,
                Phases = currentPhases,
                IsCurrent = isCurrent,
                PlanReason = pipeline.Plan?.Reason,
                PlanningBrainPrompt = firstLogEntry?.PlanningPrompt,
                PlanningBrainResponse = firstLogEntry?.PlanningResponse,
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
            var roleName = pr.Name.ToRoleName();

            // Count total occurrences per phase in this summary for last-occurrence detection.
            var totalOccurrencesForPhase = summary.Phases.Count(p => p.Name == pr.Name);
            // Current occurrence: use PhaseResult.Occurrence if set, else default to 1.
            var occurrence = pr.Occurrence ?? 1;
            var isLastOccurrence = occurrence >= totalOccurrencesForPhase;

            // Use PhaseResult.WorkerOutput directly (persisted on the log entry).
            string? workerOutput = pr.WorkerOutput;

            var isTestPhase = pr.Name == GoalPhase.Testing;
            var isReviewPhase = pr.Name == GoalPhase.Review;

            // Filter clarifications by occurrence using the OccurrenceFilter helper.
            List<ClarificationEntry>? phaseClarifications = null;
            if (clarificationsByPhase.TryGetValue(pr.Name.ToString(), out var allPhaseClarifications))
            {
                phaseClarifications = OccurrenceFilter.FilterByOccurrence(allPhaseClarifications, occurrence);
            }

            // Filter progress reports by occurrence using the OccurrenceFilter helper.
            var allPhaseProgress = pipeline?.ProgressReports
                .Where(p => p.Iteration == summary.Iteration && p.Phase == pr.Name.ToString())
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
                pipeline: pipeline,
                occurrence: occurrence,
                isLastOccurrence: isLastOccurrence));
        }

        return phases;
    }

    // ── BuildPhasesFromPipeline ────────────────────────────────────────────────

    /// <summary>
    /// Builds the list of <see cref="PhaseViewInfo"/> entries for the live pipeline's
    /// current iteration, using PhaseLog for completed/active phases and the plan for pending ones.
    /// </summary>
    public static List<PhaseViewInfo> BuildPhasesFromPipeline(string goalId, GoalPipeline pipeline, int currentIter)
    {
        // Determine the planning phase status.
        var planningStatus = pipeline.Phase == GoalPhase.Planning ? "active" : "completed";

        // Collect all clarifications from this pipeline, grouped by phase name.
        var clarificationsByPhase = pipeline.Clarifications
            .Where(c => c.Iteration == currentIter)
            .GroupBy(c => c.Phase, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        clarificationsByPhase.TryGetValue("Planning", out var planningClarifications);

        // Get the planning prompt from the first PhaseLog entry's PlanningPrompt/PlanningResponse
        var firstLogEntry = pipeline.PhaseLog
            .FirstOrDefault(e => e.Iteration == currentIter);

        var phases = new List<PhaseViewInfo>
        {
            new PhaseViewInfo
            {
                Name = "Planning",
                RoleName = "brain",
                Status = planningStatus,
                BrainPrompt = firstLogEntry?.PlanningPrompt,
                WorkerPrompt = firstLogEntry?.PlanningResponse,
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
            // Get PhaseLog entries for the current iteration
            var logEntries = pipeline.PhaseLog
                .Where(e => e.Iteration == currentIter)
                .ToList();

            // Build a set of phases already in the log (with occurrence tracking)
            var loggedPhases = new HashSet<(GoalPhase, int)>();
            foreach (var entry in logEntries)
                loggedPhases.Add((entry.Name, entry.Occurrence ?? 1));

            // Show PhaseLog entries (completed and active)
            foreach (var entry in logEntries)
            {
                var occurrence = entry.Occurrence ?? 1;
                var phaseName = entry.Name.ToString();

                string status;
                if (entry.CompletedAt.HasValue)
                    status = entry.Result == PhaseOutcome.Fail ? "failed"
                           : entry.Result == PhaseOutcome.Skip ? "skipped"
                           : "completed";
                else if (pipeline.IsWaitingForClarification)
                    status = "waiting";
                else
                    status = "active";

                var isTestPhase = entry.Name == GoalPhase.Testing;
                var isReviewPhase = entry.Name == GoalPhase.Review;

                // Determine last occurrence
                var totalOccurrencesForPhase = logEntries.Count(e => e.Name == entry.Name);
                var isLastOccurrence = occurrence >= totalOccurrencesForPhase;

                List<ClarificationEntry>? phaseClarifications = null;
                if (clarificationsByPhase.TryGetValue(phaseName, out var allPhaseClarifications))
                    phaseClarifications = OccurrenceFilter.FilterByOccurrence(allPhaseClarifications, occurrence);

                var allPhaseProgress = pipeline.ProgressReports
                    .Where(p => p.Iteration == currentIter && p.Phase == phaseName)
                    .OrderBy(p => p.Timestamp)
                    .ToList();
                var phaseProgress = OccurrenceFilter.FilterByOccurrence(allPhaseProgress, occurrence);

                var metrics = pipeline.Metrics;
                var hasMetrics = status is "completed" or "active" or "failed" or "waiting";

                phases.Add(new PhaseViewInfo
                {
                    Name = entry.Name.ToDisplayName(),
                    RoleName = entry.Name.ToRoleName(),
                    Status = status,
                    Occurrence = occurrence,
                    WorkerOutput = entry.WorkerOutput,
                    DurationSeconds = entry.DurationSeconds > 0 ? entry.DurationSeconds : null,
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
                    ProgressReports = phaseProgress,
                    BrainPrompt = entry.BrainPrompt,
                    WorkerPrompt = entry.WorkerPrompt,
                    Clarifications = phaseClarifications ?? [],
                });
            }

            // Show pending plan phases not yet in the log
            var planPhases = pipeline.Plan?.Phases ?? [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging];
            var pendingOccurrenceCounters = new Dictionary<GoalPhase, int>();
            foreach (var planPhase in planPhases)
            {
                pendingOccurrenceCounters[planPhase] = pendingOccurrenceCounters.GetValueOrDefault(planPhase) + 1;
                var occ = pendingOccurrenceCounters[planPhase];
                if (loggedPhases.Contains((planPhase, occ)))
                    continue;

                phases.Add(new PhaseViewInfo
                {
                    Name = planPhase.ToDisplayName(),
                    RoleName = planPhase.ToRoleName(),
                    Status = "pending",
                    Occurrence = occ,
                });
            }
        }

        return phases;
    }

    // ── BuildPhaseViewInfo ──────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="PhaseViewInfo"/> from a persisted <see cref="PhaseResult"/>
    /// (summarised-iteration path).
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
        GoalPipeline? pipeline,
        int occurrence = 1,
        bool isLastOccurrence = true)
    {
        return new PhaseViewInfo
        {
            Name = phase.Name.ToDisplayName(),
            RoleName = phase.Name.ToRoleName(),
            Status = phase.Result switch { PhaseOutcome.Pass => "completed", PhaseOutcome.Fail => "failed", PhaseOutcome.Skip => "skipped", _ => "completed" },
            DurationSeconds = phase.DurationSeconds > 0 ? phase.DurationSeconds : null,
            Occurrence = occurrence,
            WorkerOutput = workerOutput,
            BrainPrompt = phase.BrainPrompt,
            WorkerPrompt = phase.WorkerPrompt,
            TotalTests = isTestPhase && isLastOccurrence ? (summary?.TestCounts?.Total ?? 0) : 0,
            PassedTests = isTestPhase && isLastOccurrence ? (summary?.TestCounts?.Passed ?? 0) : 0,
            FailedTests = isTestPhase && isLastOccurrence ? (summary?.TestCounts?.Failed ?? 0) : 0,
            BuildSuccess = isTestPhase && isLastOccurrence && (summary?.BuildSuccess ?? false),
            ReviewVerdict = isReviewPhase && isLastOccurrence ? summary?.ReviewVerdict : null,
            Clarifications = clarifications ?? [],
            ProgressReports = progress,
        };
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

}
