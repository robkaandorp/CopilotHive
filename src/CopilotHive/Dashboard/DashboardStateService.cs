using System.Reflection;
using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Knowledge;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;

using Microsoft.Extensions.AI;

namespace CopilotHive.Dashboard;

/// <summary>
/// Aggregates live state from WorkerPool, GoalPipelineManager, and IGoalStore
/// into a snapshot that Blazor components can bind to. Polls every few seconds
/// and fires <see cref="OnStateChanged"/> to trigger UI re-renders.
/// </summary>
public sealed class DashboardStateService : IDisposable
{
    private readonly WorkerPool _workerPool;
    private readonly GoalPipelineManager _pipelineManager;
    private readonly IGoalStore? _goalStore;
    private readonly DashboardLogSink _logSink;
    private readonly ProgressLog _progressLog;
    private readonly IDistributedBrain? _brain;
    private readonly Composer? _composer;
    private readonly HiveConfigFile? _config;
    private readonly KnowledgeGraph? _knowledgeGraph;
    private readonly DashboardNotifier? _notifier;
    private readonly Timer _timer;
    private readonly DateTime _startTime = DateTime.UtcNow;
    private readonly string _version =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";
    private readonly string? _sharpCoderVersion = typeof(SharpCoder.CodingAgent).Assembly.GetName().Version?.ToString();

    /// <summary>Fired when state has been polled and components should re-render.</summary>
    public event Action? OnStateChanged;

    /// <summary>Timer polling period used by the dashboard refresh timer.</summary>
    internal TimeSpan TimerPeriod { get; } = TimeSpan.FromSeconds(10);

    /// <summary>Creates the service with required dependencies.</summary>
    public DashboardStateService(
        WorkerPool workerPool,
        GoalPipelineManager pipelineManager,
        DashboardLogSink logSink,
        ProgressLog progressLog,
        DashboardNotifier? notifier = null,
        IDistributedBrain? brain = null,
        Composer? composer = null,
        HiveConfigFile? config = null,
        IGoalStore? goalStore = null,
        KnowledgeGraph? knowledgeGraph = null)
    {
        _workerPool = workerPool;
        _pipelineManager = pipelineManager;
        _logSink = logSink;
        _progressLog = progressLog;
        _notifier = notifier;
        _brain = brain;
        _composer = composer;
        _config = config;
        _goalStore = goalStore;
        _knowledgeGraph = knowledgeGraph;

        if (_notifier is not null)
            _notifier.OnStateChanged += RelayNotifier;

        _timer = new Timer(_ => PollAndNotify(), null, TimerPeriod, TimerPeriod);
    }

    private void RelayNotifier() => OnStateChanged?.Invoke();

    // ── Timer callback ─────────────────────────────────────────────────────────

    /// <summary>
    /// Timer callback: fires <see cref="OnStateChanged"/> so components re-render.
    /// </summary>
    private void PollAndNotify() => OnStateChanged?.Invoke();

    // ── Snapshot ───────────────────────────────────────────────────────────────

    /// <summary>Creates a snapshot of current system state.</summary>
    /// <remarks>
    /// The worker projection is built from ONE pool-owned, lock-consistent capture of the pool's
    /// state, and every worker count on the returned snapshot is derived from that same capture — so
    /// the totals and the per-worker rows of one snapshot always describe the same instant. That
    /// consistency is relative to pool-owned worker-state mutations ONLY; it makes no claim about
    /// external writes to a leaked worker instance, and the goals and pipelines in this snapshot come
    /// from their own separate reads.
    /// <para>
    /// THE GOAL MERGE IS SIDE-EFFECT FREE: a pipeline-backed row's <see cref="Goal.Status"/> is
    /// DERIVED from the pipeline phase and is written onto a SEPARATE display instance created by
    /// <see cref="CreateDisplayRow(Goal, GoalStatus)"/> — never onto the store-returned goal and never
    /// onto the live <see cref="GoalPipeline.Goal"/>. Building a snapshot therefore mutates neither
    /// input, so the stored row keeps its lifecycle status (what <see cref="GetGoalDetail"/>'s resume
    /// check reads) and the live goal keeps its own. On an ID overlap every non-status field comes
    /// from the STORE row; when the store has no matching row those fields come from the LIVE goal.
    /// </para>
    /// </remarks>
    public async Task<DashboardSnapshot> GetSnapshot()
    {
        // ONE pool-owned capture of the worker status, taken under the pool's activity lock: the
        // per-worker rows AND every worker aggregate below are derived from THIS list, never from a
        // second read of the pool — so no total can disagree with the rows beside it, and the counts
        // can never describe two different instants. The capture is detached (copied values), so
        // projecting it holds no live worker reference.
        var workers = _workerPool.CaptureWorkerStatus();
        var pipelines = _pipelineManager.GetAllPipelines();

        // THE SOURCE ROWS, keyed by goal id: the store rows first (in the store's order), then a
        // pipeline-only goal appended for any id the store did not return. These instances are READ
        // ONLY from here on — nothing below writes to them.
        var goalsById = new Dictionary<string, Goal>();

        // Add all goals from SQLite store (includes all statuses)
        if (_goalStore is not null)
        {
            var allGoals = await _goalStore.GetAllGoalsAsync();
            foreach (var g in allGoals)
                goalsById[g.Id] = g;
        }

        // The DISPLAY status each pipeline-backed id derives from its pipeline phase, kept BESIDE the
        // source rows instead of being written onto them. Done => Completed, Failed => Failed, every
        // other phase (including Planning) => InProgress.
        var displayStatusByGoalId = new Dictionary<string, GoalStatus>(StringComparer.Ordinal);

        foreach (var p in pipelines)
        {
            // STORE ROW PRECEDENCE: a store row already present for this id keeps supplying every
            // non-status field; the live pipeline goal only supplies them when the store has no match.
            goalsById.TryAdd(p.GoalId, p.Goal);

            displayStatusByGoalId[p.GoalId] = p.Phase switch
            {
                GoalPhase.Done => GoalStatus.Completed,
                GoalPhase.Failed => GoalStatus.Failed,
                _ => GoalStatus.InProgress,
            };
        }

        // The snapshot rows, in the SAME order and count as the source rows above. A pipeline-backed
        // row is a SEPARATE display instance carrying the phase-derived status, so reading the
        // snapshot leaves the stored goal's lifecycle status — the authoritative input of
        // GetGoalDetail's resume check — and the live pipeline goal's own status untouched. A goal
        // with no pipeline has nothing to derive, so its source row is published as it is.
        var goals = new List<Goal>(goalsById.Count);
        foreach (var (goalId, source) in goalsById)
        {
            goals.Add(displayStatusByGoalId.TryGetValue(goalId, out var displayStatus)
                ? CreateDisplayRow(source, displayStatus)
                : source);
        }

        return new DashboardSnapshot
        {
            Goals = goals,
            Workers = workers.Select(ToWorkerInfo).ToList(),
            Pipelines = pipelines.Select(p => new PipelineInfo
            {
                GoalId = p.GoalId,
                Description = p.Description,
                Phase = p.Phase.ToString(),
                Phases = p.Plan?.Phases.Select(ph => ph.ToString()).ToList() ?? [],
                Iteration = p.Iteration,
                ActiveTaskId = p.ActiveTaskId,
                CreatedAt = p.CreatedAt,
                GoalStartedAt = p.GoalStartedAt,
                CompletedAt = p.CompletedAt,
                TotalTests = p.Metrics.TotalTests,
                PassedTests = p.Metrics.PassedTests,
                FailedTests = p.Metrics.FailedTests,
                CoveragePercent = p.Metrics.CoveragePercent,
            }).ToList(),
            DraftGoals = goals.Count(g => g.Status == GoalStatus.Draft),
            PendingGoals = goals.Count(g => g.Status == GoalStatus.Pending),
            ActiveGoals = goals.Count(g => g.Status == GoalStatus.InProgress),
            CompletedGoals = goals.Count(g => g.Status == GoalStatus.Completed),
            FailedGoals = goals.Count(g => g.Status == GoalStatus.Failed),
            TotalWorkers = workers.Count,
            BusyWorkers = workers.Count(w => w.IsBusy),
            IdleWorkers = workers.Count(w => !w.IsBusy),
            AvailableWorkers = workers.Count(w => w.IsAvailable),
            AwaitingReadyWorkers = workers.Count(w => w.AwaitingWorkerReady),
        };
    }

    /// <summary>
    /// THE ONE WORKER MAPPING into the snapshot's <see cref="WorkerInfo"/> row, used by
    /// <see cref="GetSnapshot"/> for every captured worker.
    /// </summary>
    /// <param name="captured">One value of the pool-owned capture (never a live worker alias).</param>
    /// <returns>The dashboard worker row, copied by value.</returns>
    private static WorkerInfo ToWorkerInfo(WorkerStatusSnapshot captured) => new()
    {
        Id = captured.Id,
        Role = captured.Role.ToString(),
        IsBusy = captured.IsBusy,
        CurrentTaskId = captured.CurrentTaskId,
        LastHeartbeat = captured.LastHeartbeat,
        ConnectedAt = captured.ConnectedAt,
        CurrentModel = captured.CurrentModel,
        ContextUsagePercent = captured.ContextUsagePercent,
        IsAvailable = captured.IsAvailable,
        AwaitingWorkerReady = captured.AwaitingWorkerReady,
    };

    // ── Workers surface projection ─────────────────────────────────────────────

    /// <summary>
    /// THE WORKER-ONLY DISPLAY CAPTURE behind <c>IOperationalFacade.GetWorkerDisplayAsync</c>: ONE
    /// pool-owned capture of the worker status, from which BOTH the detached rows and all five counts
    /// are derived.
    /// </summary>
    /// <remarks>
    /// <para>
    /// IT READS THE POOL AND THE LOCAL ROLE CONFIGURATION, AND NOTHING ELSE: no goal store read, no
    /// pipeline read and no Composer statistics read happens here — not even indirectly through
    /// <see cref="GetOrchestratorInfo"/>, whose Brain/Composer statistics half this projection
    /// deliberately omits. The model/reasoning display values come from the SAME per-role configuration
    /// source, copied out into local dictionaries rather than returned, so no live configuration object
    /// escapes through the projection. This is deliberately narrower than <see cref="GetSnapshot"/>,
    /// which merges goals and pipelines for the whole dashboard.
    /// </para>
    /// <para>
    /// THE COUNTS CANNOT DISAGREE WITH THE ROWS: every one of them is computed from the same captured
    /// list, so they describe one instant. The meanings are the existing ones —
    /// <c>Idle</c> counts every non-busy worker (withheld workers included), <c>Available</c> only the
    /// captured assignable verdict and <c>AwaitingReady</c> the readiness-wait subset.
    /// </para>
    /// <para>
    /// THE REASONING RULE IS THE WORKERS PAGE'S, PRESERVED: the displayed model is the task-specific
    /// model, else the role's configured model; the premium comparison runs FIRST (so identical
    /// standard/premium models resolve to the premium effort) and the standard comparison second; an
    /// unmatched model, an unassigned role or an unset effort all render the existing em dash.
    /// </para>
    /// </remarks>
    /// <returns>The detached worker display projection for the captured instant.</returns>
    public WorkerDisplayProjection GetWorkerDisplay()
    {
        // ONE capture for BOTH the rows and every count below.
        var workers = _workerPool.CaptureWorkerStatus();

        // The role display configuration, COPIED: the page's model/reasoning resolution reads these
        // local copies, never the live config dictionaries.
        var roleModels = RoleModelDisplayByRole();
        var premiumModels = RolePremiumModelDisplayByRole();
        var standardReasoning = RoleReasoningDisplayByRole();
        var premiumReasoning = RolePremiumReasoningDisplayByRole();

        var rows = new List<WorkerDisplayRow>(workers.Count);
        foreach (var captured in workers)
        {
            // The role NAME the row shows ("Unspecified" for an unassigned worker) and the lowercase
            // key the configuration is looked up by — exactly the page's own convention.
            var roleName = captured.Role.ToString();
            var roleKey = roleName.ToLowerInvariant();

            var displayModel = captured.CurrentModel
                ?? (roleName != nameof(WorkerRole.Unspecified) && roleModels.TryGetValue(roleKey, out var roleModel)
                    ? roleModel
                    : null);

            var status = WorkerInfo.DescribeStatus(
                captured.IsBusy, captured.IsAvailable, captured.AwaitingWorkerReady);

            rows.Add(new WorkerDisplayRow(
                captured.Id,
                roleName,
                captured.IsBusy,
                captured.CurrentTaskId,
                captured.LastHeartbeat,
                captured.ConnectedAt,
                captured.CurrentModel,
                captured.ContextUsagePercent,
                captured.IsAvailable,
                captured.AwaitingWorkerReady,
                displayModel,
                ReasoningLabelFor(displayModel, roleKey, premiumModels, roleModels, premiumReasoning, standardReasoning),
                status.Label,
                status.CssClass));
        }

        return new WorkerDisplayProjection(
            TotalWorkers: workers.Count,
            BusyWorkers: workers.Count(w => w.IsBusy),
            IdleWorkers: workers.Count(w => !w.IsBusy),
            AvailableWorkers: workers.Count(w => w.IsAvailable),
            AwaitingReadyWorkers: workers.Count(w => w.AwaitingWorkerReady),
            Workers: rows);
    }

    /// <summary>
    /// Resolves the reasoning-effort label for a worker row from the DISPLAYED model and the copied
    /// role configuration: premium-before-standard matching, and the existing em dash for anything
    /// else (no displayed model, an unmatched model, an unassigned role or an unset effort).
    /// </summary>
    /// <param name="displayedModel">The model the row displays, or <c>null</c>.</param>
    /// <param name="roleKey">The lowercase role key the configuration is indexed by.</param>
    /// <param name="premiumModels">Copied premium model per role key.</param>
    /// <param name="standardModels">Copied standard model per role key.</param>
    /// <param name="premiumReasoning">Copied premium reasoning effort per role key.</param>
    /// <param name="standardReasoning">Copied standard reasoning effort per role key.</param>
    /// <returns>The reasoning label the Workers page renders for this row.</returns>
    private static string ReasoningLabelFor(
        string? displayedModel,
        string roleKey,
        IReadOnlyDictionary<string, string?> premiumModels,
        IReadOnlyDictionary<string, string> standardModels,
        IReadOnlyDictionary<string, ReasoningEffort?> premiumReasoning,
        IReadOnlyDictionary<string, ReasoningEffort?> standardReasoning)
    {
        if (string.IsNullOrEmpty(displayedModel))
            return ReasoningEffortOptions.Label(null);

        // PREMIUM FIRST: a role whose premium and standard models are identical resolves to the
        // premium effort, exactly as the page's helper did.
        if (string.Equals(displayedModel, premiumModels.GetValueOrDefault(roleKey), StringComparison.OrdinalIgnoreCase))
            return ReasoningEffortOptions.Label(premiumReasoning.GetValueOrDefault(roleKey));

        if (string.Equals(displayedModel, standardModels.GetValueOrDefault(roleKey), StringComparison.OrdinalIgnoreCase))
            return ReasoningEffortOptions.Label(standardReasoning.GetValueOrDefault(roleKey));

        return ReasoningEffortOptions.Label(null);
    }

    /// <summary>
    /// Copies the per-role STANDARD models exactly as <see cref="GetOrchestratorInfo"/> renders them
    /// — every known role, with the display-only "(not configured)" sentinel for an unset model.
    /// </summary>
    /// <remarks>
    /// IT IS NOT <see cref="GetOrchestratorInfo"/>: that projection also reads the Brain's and the
    /// Composer's live statistics, which are deliberately outside this worker-only surface. Only the
    /// configuration-derived half is needed to render a worker row's model, so only that half is
    /// computed — with the SAME per-role source (<see cref="HiveConfigFile.GetModelForRole(string)"/>)
    /// and the SAME sentinel, so a worker row and the Orchestrator page's role table cannot disagree
    /// about a role's configured model.
    /// </remarks>
    /// <returns>A detached role-key → display-model map.</returns>
    private Dictionary<string, string> RoleModelDisplayByRole()
    {
        var roleModels = new Dictionary<string, string>(RolesReadForDisplay.Length);
        foreach (var role in RolesReadForDisplay)
        {
            var model = _config?.Workers is null ? null : _config.GetModelForRole(role);
            roleModels[role] = string.IsNullOrWhiteSpace(model) ? NotConfigured : model!;
        }

        return roleModels;
    }

    /// <summary>
    /// The roles whose configured model the display projections render — the same five
    /// <see cref="GetOrchestratorInfo"/> reports.
    /// </summary>
    private static readonly string[] RolesReadForDisplay = ["coder", "tester", "reviewer", "docwriter", "improver"];

    /// <summary>Copies the per-role PREMIUM models out of the configuration (no live object escapes).</summary>
    /// <returns>A detached role-key → premium-model map.</returns>
    private Dictionary<string, string?> RolePremiumModelDisplayByRole()
    {
        var premiumModels = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (_config?.Workers is null)
            return premiumModels;

        foreach (var (roleName, workerCfg) in _config.Workers)
        {
            if (workerCfg is null) continue;
            premiumModels[roleName] = workerCfg.PremiumModel;
        }

        return premiumModels;
    }

    /// <summary>Copies the per-role STANDARD reasoning efforts out of the configuration.</summary>
    /// <returns>A detached role-key → reasoning-effort map.</returns>
    private Dictionary<string, ReasoningEffort?> RoleReasoningDisplayByRole()
    {
        var reasoning = new Dictionary<string, ReasoningEffort?>(StringComparer.OrdinalIgnoreCase);
        if (_config?.Workers is null)
            return reasoning;

        foreach (var (roleName, workerCfg) in _config.Workers)
        {
            if (workerCfg is null) continue;
            reasoning[roleName] = ParseLenient(workerCfg.ReasoningEffort);
        }

        return reasoning;
    }

    /// <summary>Copies the per-role PREMIUM reasoning efforts out of the configuration.</summary>
    /// <returns>A detached role-key → reasoning-effort map.</returns>
    private Dictionary<string, ReasoningEffort?> RolePremiumReasoningDisplayByRole()
    {
        var reasoning = new Dictionary<string, ReasoningEffort?>(StringComparer.OrdinalIgnoreCase);
        if (_config?.Workers is null)
            return reasoning;

        foreach (var (roleName, workerCfg) in _config.Workers)
        {
            if (workerCfg is null) continue;
            reasoning[roleName] = ParseLenient(workerCfg.PremiumReasoningEffort);
        }

        return reasoning;
    }

    /// <summary>
    /// Builds the SNAPSHOT's display row for one source goal: a SEPARATE <see cref="Goal"/> instance
    /// carrying every non-status field of <paramref name="source"/> and
    /// <paramref name="displayStatus"/> as its status.
    /// </summary>
    /// <remarks>
    /// THE WHOLE POINT IS WHAT IT DOES NOT TOUCH. The status a pipeline-backed row shows is DERIVED
    /// from the pipeline phase, so writing it onto <paramref name="source"/> would corrupt the two
    /// inputs this merge combines: the store-returned goal — whose persisted lifecycle status is the
    /// authoritative resume-eligibility input of <see cref="GetGoalDetail"/>, read through
    /// <c>GetGoalAsync</c> — and the live <see cref="GoalPipeline.Goal"/>, which the pipeline itself
    /// keeps using. This helper therefore only READS the source and returns a new instance; a snapshot
    /// read can never change either one.
    /// <para>
    /// Every non-status field is COPIED BY VALUE (its current value is read into the new instance),
    /// and the collection-valued ones — repositories, metadata, notes, summaries, documents, usage —
    /// are shared BY REFERENCE with the source. That preserves what callers already see (the same
    /// summaries, metadata, repositories and usage a caller read before) and is deliberately NOT a
    /// deep copy: isolating later collection mutation is out of this change's scope, so this row must
    /// not be read as a detached copy of those collections.
    /// </para>
    /// </remarks>
    /// <param name="source">The store row, or the live pipeline goal when the store has no match.</param>
    /// <param name="displayStatus">The status derived from the pipeline phase.</param>
    /// <returns>A new <see cref="Goal"/>; <paramref name="source"/> is unchanged.</returns>
    private static Goal CreateDisplayRow(Goal source, GoalStatus displayStatus) => new()
    {
        Id = source.Id,
        Description = source.Description,
        Priority = source.Priority,
        Scope = source.Scope,
        Status = displayStatus,
        RepositoryNames = source.RepositoryNames,
        TargetRepositoryNames = source.TargetRepositoryNames,
        DependsOn = source.DependsOn,
        Metadata = source.Metadata,
        CreatedAt = source.CreatedAt,
        StartedAt = source.StartedAt,
        CompletedAt = source.CompletedAt,
        Iterations = source.Iterations,
        FailureReason = source.FailureReason,
        Notes = source.Notes,
        PhaseDurations = source.PhaseDurations,
        IterationSummaries = source.IterationSummaries,
        TotalDurationSeconds = source.TotalDurationSeconds,
        MergeCommitHash = source.MergeCommitHash,
        ReleaseId = source.ReleaseId,
        Documents = source.Documents,
        BranchCleanedUp = source.BranchCleanedUp,
        ReviewStatus = source.ReviewStatus,
        BrainUsage = source.BrainUsage,
        ReviewUsage = source.ReviewUsage,
    };

    // ── Goal detail ────────────────────────────────────────────────────────────

    /// <summary>Builds a rich detail view for a specific goal, including per-iteration phase info.</summary>
    public async Task<GoalDetailInfo?> GetGoalDetail(string goalId)
    {
        var snap = await GetSnapshot();
        var goal = snap.Goals.FirstOrDefault(g => g.Id == goalId);
        if (goal is null)
            return null;

        // ONE authoritative read of the stored goal, serving BOTH the display summaries and the
        // resume-eligibility input below.
        //
        // GetSnapshot uses GetAllGoalsAsync which does NOT populate IterationSummaries, so the
        // detail view needs the full goal with summaries loaded from the store. The display use is
        // unchanged: the stored goal is handed to the builder EXACTLY under the old condition
        // (only when the snapshot goal carries no summaries), so what is rendered does not shift.
        var storedGoal = _goalStore is null ? null : await _goalStore.GetGoalAsync(goalId);
        var fullGoalWithSummaries = goal.IterationSummaries.Count == 0 ? storedGoal : null;

        var pipeline = _pipelineManager.GetByGoalId(goalId);
        var runningTaskUsage = ResolveRunningTaskUsage(pipeline);

        // THE RESUME FLAG — FAIL CLOSED, and exactly as the backend computes it.
        //
        // The eligibility input is the AUTHORITATIVE stored goal: the same GetGoalAsync path, and the
        // same instance, that GoalDispatcher.ResumeGoalAsync reads. It is deliberately NOT the
        // snapshot's copy, whose Status GetSnapshot overwrites from the pipeline phase
        // (Done => Completed, Failed => Failed, else InProgress) — a DERIVED value, not the lifecycle
        // truth. Evaluating the snapshot goal would show the button for a goal the backend refuses,
        // precisely the boundary the lifecycle produces when a pipeline reaches Failed BEFORE the
        // asynchronous goal finalization persists that status.
        //
        // FAIL CLOSED WHEN THERE IS NO AUTHORITATIVE GOAL: ResumeGoalAsync refuses UNCONDITIONALLY in
        // both absence states — `if (goalStore is null) return false;` when no goal store is
        // configured, and `if (goal is null || !IsResumeCandidateGoal(goal)) return false;` when the
        // stored goal is missing (re-checked inside the resume lock). The dashboard can compute that
        // answer exactly: it knows whether a store is configured and whether GetGoalAsync answered.
        // So a null stored goal — no store configured, or the goal deleted while its pipeline is
        // still live — yields CanResume = false, which is BACKEND parity, not merely the safe
        // direction: in no-store mode the dispatcher refuses EVERY goal, and a goal deleted from the
        // store no longer exists for the backend either. Without this, the page would evaluate the
        // phase-derived display Status and show a button that fails on click.
        //
        // Everything else is unchanged and still ADMITS nothing: the pipeline is resolved IN MEMORY
        // OR FROM THE STORE (the read that makes the flag correct after an orchestrator restart, when
        // a Failed pipeline is not in memory because LoadActivePipelines skips terminal phases), and
        // that lookup registers no pipeline instance and no task mapping.
        var canResume = false;
        if (storedGoal is not null)
        {
            var phaseAndBranch = _pipelineManager.GetPhaseAndCoderBranch(goalId);
            canResume = GoalResumeEligibility.IsResumable(
                storedGoal, phaseAndBranch?.Phase, phaseAndBranch?.CoderBranch);
        }

        return GoalDetailViewBuilder.Build(
            goal, goalId, pipeline, fullGoalWithSummaries, _config, runningTaskUsage, canResume);
    }

    /// <summary>
    /// Resolves the LIVE usage of the task the pipeline is currently running from ONE worker-status
    /// capture, or <c>null</c> when it cannot be attributed to exactly one worker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE ACTIVE TASK ID IS READ ONCE into a local and the capture is taken ONCE, and the two are
    /// independent reads — the pointer may move between them, which simply yields no match. A worker
    /// snapshot contributes only when its <see cref="WorkerStatusSnapshot.LiveUsageTaskId"/> equals
    /// that id ORDINALLY, so usage a worker reported for a task it is no longer running is never
    /// shown as the running phase's usage.
    /// </para>
    /// <para>
    /// EXACTLY ONE MATCH IS REQUIRED. Zero matches (no worker is running the task — e.g. the task was
    /// just dispatched and no heartbeat has arrived yet), SEVERAL matches (two workers report the same
    /// task id, which would make a single figure misleading), a <c>null</c>/empty active task id and
    /// the absence of a pipeline all yield <c>null</c> — no live usage is shown. The snapshots are
    /// never summed and never guessed: a sum would present a fabricated figure as measured.
    /// </para>
    /// <para>
    /// The returned summary is the detached snapshot the pool captured, so no
    /// <see cref="ConnectedWorker"/> reference is read, stored or exposed here.
    /// </para>
    /// </remarks>
    /// <param name="pipeline">The goal's pipeline, or <c>null</c> when the goal is not active.</param>
    /// <returns>The running task's live usage, or <c>null</c> when it cannot be attributed.</returns>
    private SharpCoder.UsageSummary? ResolveRunningTaskUsage(GoalPipeline? pipeline)
    {
        // Read ONCE: the id the rest of this method attributes usage to.
        var activeTaskId = pipeline?.ActiveTaskId;
        if (string.IsNullOrEmpty(activeTaskId))
            return null;

        // ONE capture for this response, exactly like GetSnapshot's worker projection.
        var workers = _workerPool.CaptureWorkerStatus();

        var matches = workers
            .Where(w => string.Equals(w.LiveUsageTaskId, activeTaskId, StringComparison.Ordinal))
            .ToList();

        // EXACTLY ONE match, or no live usage: several matches are ambiguous, so neither is used.
        return matches.Count == 1 ? matches[0].LiveUsage : null;
    }

    // ── Log access ─────────────────────────────────────────────────────────────

    /// <summary>Returns recent log entries from the circular buffer.</summary>
    public IReadOnlyList<LogEntry> GetRecentLogs(int count = 500) => _logSink.GetRecent(count);

    /// <summary>Returns current Brain statistics, or null if Brain is not configured.</summary>
    public BrainStats? GetBrainStats() => _brain?.GetStats();

    /// <summary>Returns current Composer statistics, or null if Composer is not configured.</summary>
    public BrainStats? GetComposerStats() => _composer?.GetStats();

    /// <summary>
    /// Resets the master Brain conversation session, clearing its history and reloading the
    /// orchestrator instructions. Per-goal sessions of goals already in progress are preserved.
    /// Does nothing if the Brain is not configured.
    /// </summary>
    public async Task ResetBrainSessionAsync(CancellationToken ct = default)
    {
        if (_brain is not null)
            await _brain.ResetSessionAsync(ct);
    }

    // ── Repository URL (kept public for test compatibility) ───────────────────

    /// <summary>
    /// Resolves the URL of the first repository associated with a goal,
    /// stripping any <c>.git</c> suffix. Returns <c>null</c> if the goal has no
    /// repository names or the repository is not found in the configuration.
    /// </summary>
    public string? GetRepositoryUrl(Goal goal) =>
        GoalDetailViewBuilder.ResolveRepositoryUrl(goal, _config);

    // ── Static prompt-extraction helpers (kept on DashboardStateService for callers) ──

    // ── Release CRUD ───────────────────────────────────────────────────────────

    /// <summary>Returns all releases, optionally filtered by repository name.</summary>
    public async Task<IReadOnlyList<Release>> GetReleasesAsync(string? repository = null, CancellationToken ct = default)
    {
        if (_goalStore is null)
            return [];
        var releases = await _goalStore.GetReleasesAsync(ct);
        if (!string.IsNullOrEmpty(repository))
            releases = releases.Where(r => r.RepositoryNames.Contains(repository, StringComparer.OrdinalIgnoreCase)).ToList();
        return releases;
    }

    /// <summary>Returns a single release by ID with its associated goals, or <c>null</c> if not found.</summary>
    public async Task<Release?> GetReleaseDetailAsync(string releaseId, CancellationToken ct = default)
    {
        if (_goalStore is null)
            return null;
        return await _goalStore.GetReleaseAsync(releaseId, ct);
    }

    /// <summary>Updates a release's mutable fields via the goal store.</summary>
    public async Task UpdateReleaseAsync(Release release, CancellationToken ct = default)
    {
        if (_goalStore is not null)
            await _goalStore.UpdateReleaseAsync(release, ct);
    }

    /// <summary>Creates a new release for the given repository and version tag.</summary>
    public async Task<Release> CreateReleaseAsync(string repository, string version, CancellationToken ct = default)
    {
        if (_goalStore is null)
            throw new InvalidOperationException("Goal store is not configured.");
        var release = new Release
        {
            Id = version,
            Tag = version,
            RepositoryNames = string.IsNullOrEmpty(repository) ? [] : [repository],
        };
        return await _goalStore.CreateReleaseAsync(release, ct);
    }

    /// <summary>Returns all goals assigned to the given release.</summary>
    public async Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default)
    {
        if (_goalStore is null)
            return [];
        return await _goalStore.GetGoalsByReleaseAsync(releaseId, ct);
    }

    // ── Knowledge Graph ───────────────────────────────────────────────────────

    /// <summary>Returns the total number of knowledge documents, or 0 if the graph is not configured.</summary>
    public int GetKnowledgeDocumentCount()
        => _knowledgeGraph?.GetAllDocuments().Count ?? 0;

    /// <summary>Returns the KnowledgeGraph instance, or null if not configured.</summary>
    public KnowledgeGraph? KnowledgeGraph => _knowledgeGraph;

    // ── Orchestrator info ─────────────────────────────────────────────────────

    /// <summary>Returns the CopilotHive assembly version string.</summary>
    public string GetVersion() => _version;

    /// <summary>Display-only sentinel rendered for an unconfigured (null/whitespace) model.</summary>
    private const string NotConfigured = "(not configured)";

    /// <summary>Returns orchestrator info including uptime, versions, and model configuration.</summary>
    public OrchestratorInfo GetOrchestratorInfo()
    {
        var uptime = DateTime.UtcNow - _startTime;
        var roles = new[] { "coder", "tester", "reviewer", "docwriter", "improver" };

        // Role models come from each role's OWN configured model (GetModelForRole returns
        // string? — no orchestrator/constant fallback). Null/whitespace renders the
        // display-only "(not configured)" sentinel; the sentinel is never written back.
        var roleModels = new Dictionary<string, string>(roles.Length);
        foreach (var role in roles)
        {
            var model = _config?.Workers is null ? null : _config.GetModelForRole(role);
            roleModels[role] = string.IsNullOrWhiteSpace(model) ? NotConfigured : model!;
        }

        // Reasoning effort dictionaries (null-safe, lenient parse).
        var roleReasoning = new Dictionary<string, ReasoningEffort?>(StringComparer.OrdinalIgnoreCase);
        var rolePremiumReasoning = new Dictionary<string, ReasoningEffort?>(StringComparer.OrdinalIgnoreCase);
        var rolePremiumModels = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (_config?.Workers is not null)
        {
            foreach (var (roleName, workerCfg) in _config.Workers)
            {
                if (workerCfg is null) continue;
                roleReasoning[roleName] = ParseLenient(workerCfg.ReasoningEffort);
                rolePremiumReasoning[roleName] = ParseLenient(workerCfg.PremiumReasoningEffort);
                rolePremiumModels[roleName] = workerCfg.PremiumModel;
            }
        }

        return new OrchestratorInfo
        {
            Uptime = uptime,
            Version = _version,
            SharpCoderVersion = _sharpCoderVersion,
            ServerTime = DateTime.UtcNow,
            RoleModels = roleModels,
            BrainModel = RenderModel(_brain?.GetStats()?.Model),
            ComposerModel = RenderModel(_composer?.GetStats()?.Model),
            CompactionModel = _config?.GetCompactionModel(),
            BrainReasoningEffort = ParseLenient(_config?.Orchestrator?.ReasoningEffort),
            ComposerReasoningEffort = ParseLenient(
                !string.IsNullOrWhiteSpace(_config?.Composer?.ReasoningEffort)
                    ? _config.Composer.ReasoningEffort
                    : _config?.Orchestrator?.ReasoningEffort),
            RoleReasoningEfforts = roleReasoning,
            RolePremiumReasoningEfforts = rolePremiumReasoning,
            RolePremiumModels = rolePremiumModels,
        };
    }

    /// <summary>
    /// Renders a model identifier for display: null/whitespace renders the
    /// "(not configured)" sentinel (display-only; never written back to the API/config).
    /// </summary>
    private static string RenderModel(string? model) =>
        string.IsNullOrWhiteSpace(model) ? NotConfigured : model;

    private static ReasoningEffort? ParseLenient(string? value)
    {
        try
        {
            return ReasoningEffortConverter.Parse(value);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_notifier is not null)
            _notifier.OnStateChanged -= RelayNotifier;
        _timer.Dispose();
    }
}
