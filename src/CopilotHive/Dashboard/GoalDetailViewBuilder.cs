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
    /// <param name="runningTaskUsage">
    /// The LIVE usage of the task the pipeline is currently running, as resolved by the CALLER from a
    /// worker-heartbeat capture, or <c>null</c> when no single worker reported usage for the active
    /// task. It is applied ONLY to the running entry of the live iteration (see
    /// <see cref="PhaseViewInfo.IsLiveUsage"/>) and into that iteration's and the goal's totals. The
    /// default <c>null</c> leaves every value byte-identical to the pre-live-usage output, so callers
    /// that cannot attribute live usage (and existing tests) keep their exact behaviour.
    /// </param>
    /// <returns>A fully-populated <see cref="GoalDetailInfo"/>, or null if the goal is not found.</returns>
    public static GoalDetailInfo? Build(
        Goal goal,
        string goalId,
        GoalPipeline? pipeline,
        Goal? fullGoalWithSummaries,
        HiveConfigFile? config,
        SharpCoder.UsageSummary? runningTaskUsage = null)
    {
        // Use the full goal with summaries if provided (store path), otherwise use lightweight goal.
        // The authoritative goal is used for ALL view-model fields (not just Iterations).
        var effectiveGoal = fullGoalWithSummaries ?? goal;

        var iterationSources = BuildIterationSources(effectiveGoal, goalId, pipeline, runningTaskUsage);
        var iterations = iterationSources.Select(s => s.View).ToList();

        // The goal total sums the union of exactly the phase records each iteration was built from.
        // Every iteration contributes exactly once: a summarised iteration contributes its chosen
        // summary's phases, the live iteration contributes its own PhaseLog entries — with the
        // RUNNING entry replaced by a DETACHED substitute carrying the live usage when the caller
        // attributed one — and the two sets can never overlap (the live branch runs only when no
        // summary exists for its number).
        // ONE sum serves both the worker-phase total and the goal BREAKDOWN, so the two can never
        // disagree about which phase records (or which live substitute) they describe.
        var goalUsageTotals = PhaseUsageTotals.Sum(iterationSources.SelectMany(s => s.Phases));
        var goalUsage = SumWorkerUsage(goalUsageTotals);
        var goalIncludesLiveUsage = iterationSources.Any(s => s.IncludesLiveUsage);

        // The grand total adds the goal's OWN stored usage — its Brain calls and its pre-execution
        // reviews — to the worker phases. Null (nothing at all reported) suppresses the card, so a
        // legacy goal renders exactly as before; a goal whose usage is Brain/review-ONLY also gets a
        // breakdown, because the tokens were really spent even though no worker phase ran yet.
        var goalBreakdown = GoalUsageBreakdown.Create(goalUsageTotals, effectiveGoal.BrainUsage, effectiveGoal.ReviewUsage);

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
            ReviewStatus = effectiveGoal.ReviewStatus,
            CurrentIteration = pipeline?.Iteration ?? 0,
            CurrentPhase = pipeline?.Phase.ToDisplayName() ?? "",
            CreatedAt = pipeline?.CreatedAt ?? effectiveGoal.CreatedAt,
            CompletedAt = pipeline?.CompletedAt ?? effectiveGoal.CompletedAt,
            ActiveTaskId = pipeline?.ActiveTaskId,
            CoderBranch = pipeline?.CoderBranch,
            Notes = effectiveGoal.Notes,
            DependsOn = effectiveGoal.DependsOn,
            Documents = effectiveGoal.Documents,
            Iterations = iterations,
            Conversation = pipeline?.Conversation.ToList() ?? [],
            MergeCommitHash = pipeline?.MergeCommitHash ?? effectiveGoal.MergeCommitHash,
            RepositoryUrl = ResolveRepositoryUrl(effectiveGoal, config),
            RepositoryNames = effectiveGoal.RepositoryNames,
            FailureReason = effectiveGoal.FailureReason,
            UsageTotals = goalUsage,
            GoalUsage = goalBreakdown,
            IncludesLiveUsage = goalIncludesLiveUsage,
        };
    }

    // ── IterationTimeline ──────────────────────────────────────────────────────

    /// <summary>
    /// Builds the list of <see cref="IterationViewInfo"/> entries for a goal,
    /// covering both summarised (completed) iterations and the live pipeline iteration.
    /// </summary>
    public static List<IterationViewInfo> BuildIterationTimeline(
        Goal goal, string goalId, GoalPipeline? pipeline) =>
        BuildIterationSources(goal, goalId, pipeline).Select(s => s.View).ToList();

    /// <summary>
    /// The phase records ONE iteration view was built from, paired with that view. The phase list is
    /// the EXACT set the iteration's <see cref="IterationViewInfo.UsageTotals"/> are summed over —
    /// never a wider one — so the goal total can union these lists without counting a phase twice.
    /// </summary>
    /// <param name="View">The iteration view built from <paramref name="Phases"/>.</param>
    /// <param name="Phases">
    /// The exact phase records this iteration was summed over. For the live iteration the RUNNING entry
    /// is the DETACHED substitute carrying the caller-supplied live usage (when one was attributed),
    /// never the pipeline's own <see cref="PhaseResult"/> — the pipeline is never mutated.
    /// </param>
    /// <param name="IncludesLiveUsage">
    /// Whether <paramref name="Phases"/> contains such a live-usage substitute, i.e. whether the
    /// iteration's (and therefore the goal's) total counts the running task's live usage.
    /// </param>
    private sealed record IterationSource(
        IterationViewInfo View, IReadOnlyList<PhaseResult> Phases, bool IncludesLiveUsage);

    /// <summary>
    /// Builds the iteration views together with the exact phase records each one was built from.
    /// A summarised iteration is built from its chosen summary's phases; the live iteration is built
    /// from its own <see cref="GoalPipeline.PhaseLog"/> entries. The two sets never overlap: the live
    /// branch runs only when NO summary exists for the pipeline's current iteration, so a summarised
    /// current iteration never also contributes its PhaseLog usage.
    /// </summary>
    /// <param name="goal">The authoritative goal supplying the persisted summaries.</param>
    /// <param name="goalId">The goal identifier.</param>
    /// <param name="pipeline">The live pipeline, or <c>null</c> for a stored-only goal.</param>
    /// <param name="runningTaskUsage">
    /// The running task's live usage, already attributed to the pipeline's active task by the caller,
    /// or <c>null</c> when none was attributed. It is applied ONLY to the live iteration's running
    /// entry; summarised iterations are never affected.
    /// </param>
    private static List<IterationSource> BuildIterationSources(
        Goal goal, string goalId, GoalPipeline? pipeline, SharpCoder.UsageSummary? runningTaskUsage = null)
    {
        var sources = new List<IterationSource>();

        // Build views for completed iterations from IterationSummaries.
        // Merge persisted summaries (from goal source) with in-memory summaries (from pipeline).
        // A PERSISTED summary wins for a given iteration number: the in-memory one is added only
        // when no summary with that number exists, so the iteration is counted EXACTLY once.
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

            sources.Add(new IterationSource(
                new IterationViewInfo
                {
                    Number = summary.Iteration,
                    Phases = phases,
                    IsCurrent = false,
                    PlanningBrainPrompt = firstSummaryPhase?.PlanningPrompt,
                    PlanningBrainResponse = firstSummaryPhase?.PlanningResponse,
                    UsageTotals = SumWorkerUsage(summary.Phases),
                },
                summary.Phases,
                IncludesLiveUsage: false));
        }

        // Build view for the current/unsummarized iteration from pipeline state.
        if (pipeline is not null && !sources.Any(s => s.View.Number == pipeline.Iteration))
        {
            var currentIter = pipeline.Iteration;
            var isCurrent = pipeline.Phase is not GoalPhase.Done and not GoalPhase.Failed;

            // Read planning prompt/response from the first PhaseLog entry for this iteration
            var firstLogEntry = pipeline.PhaseLog
                .FirstOrDefault(e => e.Iteration == currentIter);

            // The EXACT phase records this iteration was built from: its own PhaseLog entries.
            // Materialised so a later PhaseLog append cannot change an already-built total.
            var currentPhaseResults = pipeline.PhaseLog
                .Where(e => e.Iteration == currentIter)
                .ToList();

            // The row application and the running-entry selection happen in the SAME call, so the
            // total can never include a live usage that no row marks (or vice versa).
            var currentPhases = BuildPhasesFromPipelineCore(
                goalId, pipeline, currentIter, runningTaskUsage, out var runningEntry);

            // THE LIVE-INCLUSIVE PHASE SET: the running entry is replaced by a DETACHED substitute
            // carrying the live usage — same Name/Occurrence so it still counts, and it is a COPY so
            // the pipeline's own PhaseResult (and its null Usage) is never written to. Without live
            // usage the ORIGINAL list is used verbatim, so every value is byte-identical to today.
            var summedPhases = currentPhaseResults;
            var includesLiveUsage = false;
            if (runningTaskUsage is not null && runningEntry is not null)
            {
                summedPhases = ReplaceWithLiveUsageSubstitute(currentPhaseResults, runningEntry, runningTaskUsage);
                includesLiveUsage = true;
            }

            sources.Add(new IterationSource(
                new IterationViewInfo
                {
                    Number = currentIter,
                    Phases = currentPhases,
                    IsCurrent = isCurrent,
                    PlanReason = pipeline.Plan?.Reason,
                    PlanningBrainPrompt = firstLogEntry?.PlanningPrompt,
                    PlanningBrainResponse = firstLogEntry?.PlanningResponse,
                    UsageTotals = SumWorkerUsage(summedPhases),
                    IncludesLiveUsage = includesLiveUsage,
                },
                summedPhases,
                includesLiveUsage));
        }

        return sources;
    }

    /// <summary>
    /// THE DETACHED LIVE-USAGE SUBSTITUTE: a fresh phase-record list in which the running entry is
    /// replaced by a COPY carrying <paramref name="runningTaskUsage"/> in place of its own
    /// (necessarily null) usage. Every other record is carried over VERBATIM and in the original
    /// order.
    /// <para>
    /// The copy exists so a total can count the running phase's live usage WITHOUT ever writing to the
    /// pipeline's own <see cref="PhaseResult"/>: the pipeline's entry keeps <c>Usage == null</c> until
    /// the phase genuinely completes, so nothing is double counted at the completion boundary (where
    /// the persisted report replaces this substitute entirely).
    /// </para>
    /// </summary>
    /// <param name="phaseResults">The live iteration's own records; the running entry must be one of them.</param>
    /// <param name="runningEntry">The exact instance to substitute; never mutated.</param>
    /// <param name="runningTaskUsage">The running task's live usage to carry on the substitute.</param>
    private static List<PhaseResult> ReplaceWithLiveUsageSubstitute(
        IReadOnlyList<PhaseResult> phaseResults, PhaseResult runningEntry, SharpCoder.UsageSummary runningTaskUsage)
    {
        var substituted = new List<PhaseResult>(phaseResults.Count);
        foreach (var phase in phaseResults)
        {
            substituted.Add(ReferenceEquals(phase, runningEntry)
                ? new PhaseResult
                {
                    // Same identity as the entry it stands in for, so the total still counts it at the
                    // same position — only the usage differs.
                    Name = phase.Name,
                    Result = phase.Result,
                    Iteration = phase.Iteration,
                    Occurrence = phase.Occurrence,
                    Usage = runningTaskUsage,
                }
                : phase);
        }

        return substituted;
    }

    /// <summary>
    /// Sums the usage of the given phase records and applies the shared "no usage to show"
    /// convention: the result is <c>null</c> when NO worker phase reported usage, so renderers show
    /// nothing at all and legacy data renders exactly as before.
    /// <para>
    /// Non-worker phases (Planning, Merging, Done, Failed) neither contribute tokens nor make the
    /// result non-null — <see cref="PhaseUsageTotals.Sum"/> ignores them entirely. As soon as at
    /// least one WORKER phase reported, the result is non-null, and an EMPTY summary counts as
    /// reported-with-zero exactly as <see cref="PhaseUsageTotals.Sum"/> counts it.
    /// </para>
    /// <para>
    /// The dashboard page calls this for its stored-iteration totals too, so the convention exists
    /// exactly once and both render paths agree — a stored GOAL total goes through
    /// <see cref="GoalUsageBreakdown.Create"/> instead, which needs the worker counts together with
    /// the goal's Brain and review usage.
    /// </para>
    /// </summary>
    internal static PhaseUsageTotals.Result? SumWorkerUsage(IEnumerable<PhaseResult> phases)
    {
        var totals = PhaseUsageTotals.Sum(phases);
        return totals.PhasesWithUsage > 0 ? totals : null;
    }

    /// <summary>
    /// Applies the same "no usage to show" convention to totals that were ALREADY summed: <c>null</c>
    /// when no worker phase reported usage, the totals otherwise. It exists so a caller that also
    /// needs the counts (<see cref="GoalUsageBreakdown.Create"/>) can sum ONCE and derive both the
    /// worker-only result and the goal breakdown from that single sum.
    /// </summary>
    /// <param name="totals">The already-summed worker-phase totals; must not be <c>null</c>.</param>
    /// <returns>The totals when at least one worker phase reported, otherwise <c>null</c>.</returns>
    internal static PhaseUsageTotals.Result? SumWorkerUsage(PhaseUsageTotals.Result totals)
    {
        ArgumentNullException.ThrowIfNull(totals);
        return totals.PhasesWithUsage > 0 ? totals : null;
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
    /// <para>
    /// No live usage is applied: this overload reports PERSISTED per-phase usage only, exactly as it
    /// did before live usage existed. Callers that resolved a running task's live usage go through
    /// <see cref="Build"/> (or the private core) so the row markup and the iteration total agree.
    /// </para>
    /// </summary>
    public static List<PhaseViewInfo> BuildPhasesFromPipeline(string goalId, GoalPipeline pipeline, int currentIter) =>
        BuildPhasesFromPipelineCore(goalId, pipeline, currentIter, runningTaskUsage: null, out _);

    /// <summary>
    /// THE LIVE-ITERATION BUILDER, with the caller's attributed live usage applied to the ONE running
    /// row. It is the core <see cref="BuildPhasesFromPipeline(string, GoalPipeline, int)"/> builds on,
    /// so the rendered rows and the iteration total can never disagree about which entry is running.
    /// </summary>
    /// <param name="goalId">The goal identifier.</param>
    /// <param name="pipeline">The live pipeline.</param>
    /// <param name="currentIter">The iteration being viewed.</param>
    /// <param name="runningTaskUsage">
    /// The running task's live usage, or <c>null</c> when none was attributed — in which case every row
    /// keeps its persisted usage verbatim and no row is marked live.
    /// </param>
    /// <param name="runningEntry">
    /// The pipeline's own <see cref="PhaseResult"/> instance selected as the running entry — the exact
    /// entry the substitute for the totals stands in for — or <c>null</c> when the live iteration has
    /// no running worker entry. The instance is NEVER mutated; it is reported so the caller can
    /// substitute it, and only it, in the summed phase set.
    /// </param>
    private static List<PhaseViewInfo> BuildPhasesFromPipelineCore(
        string goalId,
        GoalPipeline pipeline,
        int currentIter,
        SharpCoder.UsageSummary? runningTaskUsage,
        out PhaseResult? runningEntry)
    {
        runningEntry = null;

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

            // THE RUNNING ENTRY: the LAST current-iteration entry that has not completed, carries no
            // persisted usage of its own, and belongs to a WORKER phase. A completed entry is history;
            // an entry that already carries a persisted report is a finished phase (the completion
            // boundary) and is never overwritten, so no usage can be counted twice; the Planning row is
            // synthesised and is not a worker phase. Several qualifying entries → the LAST one is the
            // running one: PhaseLog is chronological, so the most recently appended unfinished worker
            // entry is the phase the pipeline is executing now.
            var runningLogEntry = logEntries.LastOrDefault(e =>
                e.CompletedAt is null
                && e.Usage is null
                && !string.IsNullOrEmpty(e.Name.ToRoleName()));

            // The live usage applies to that ONE entry, and only when the caller attributed one.
            if (runningTaskUsage is not null)
                runningEntry = runningLogEntry;

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

                // LIVE USAGE lands on the running row ONLY, and never replaces a persisted report:
                // the selection above already excluded every entry that carries one.
                var isLiveUsage = runningTaskUsage is not null && ReferenceEquals(entry, runningLogEntry);

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
                    // The phase's reported usage, verbatim: null stays null (not reported) — except on
                    // the running row, which shows the running task's live usage instead (still null
                    // when no live usage was attributed).
                    Usage = isLiveUsage ? runningTaskUsage : entry.Usage,
                    IsLiveUsage = isLiveUsage,
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
            // The phase's reported usage, verbatim: null stays null (not reported).
            Usage = phase.Usage,
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
