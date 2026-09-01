using System.Collections.Concurrent;
using System.Text;
using CopilotHive.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Improvement;
using CopilotHive.Knowledge;
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
    internal static TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AgentsSyncInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BranchCleanupInterval = TimeSpan.FromHours(1);

    private readonly GoalManager _goalManager;
    private readonly GoalPipelineManager _pipelineManager;
    private readonly TaskQueue _taskQueue;
    private readonly IWorkerGateway _workerGateway;
    private readonly IDistributedBrain? _brain;
    private readonly IBrainRepoManager _repoManager;
    private readonly ILogger<GoalDispatcher> _logger;
    private readonly HiveConfigFile? _config;
    private readonly KnowledgeGraph? _knowledgeGraph;
    private readonly ConfigRepoManager? _configRepo;
    private readonly IGoalStore? _goalStore;

    private readonly BranchCoordinator _branchCoordinator = new();
    private readonly TaskBuilder _taskBuilder = new(new BranchCoordinator());
    private readonly ConcurrentQueue<string> _redispatchQueue = new();
    private readonly SemaphoreSlim _resumeLock = new(1, 1);
    private readonly TimeSpan _startupDelay;
    private readonly IClarificationRouter? _clarificationRouter;
    private readonly ClarificationQueueService? _clarificationQueue;
    private readonly ProgressLog? _progressLog;
    private readonly ClarificationHandler _clarificationHandler;
    private readonly GoalLifecycleService _lifecycleService;
    private readonly PipelineDriver _pipelineDriver;
    private readonly DispatcherMaintenance _maintenance;
    private readonly TaskDispatchService _taskDispatchService;
    private readonly TaskCompletionService _taskCompletionService;
    private readonly GoalDispatchService _goalDispatchService;
    private readonly DashboardNotifier? _dashboardNotifier;
    private readonly GoalReadyNotifier? _goalReadyNotifier;
    private DateTime _lastBranchCleanup = DateTime.MinValue;

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
    /// <param name="knowledgeGraph">Optional knowledge graph for reloading on sync cycles.</param>
    /// <param name="goalStore">Optional goal store for direct CRUD operations such as branch cleanup.</param>
    /// <param name="dashboardNotifier">Optional dashboard notifier for state-change events.</param>
    /// <param name="goalReadyNotifier">Optional notifier used to wake the dispatcher when a goal becomes pending.</param>
    /// <param name="eventBus">Optional event bus for publishing system events.</param>
    /// <param name="ciMonitor">Optional CI monitor service for post-merge CI status tracking.</param>
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
        ProgressLog? progressLog = null,
        KnowledgeGraph? knowledgeGraph = null,
        IGoalStore? goalStore = null,
        DashboardNotifier? dashboardNotifier = null,
        GoalReadyNotifier? goalReadyNotifier = null,
        IEventBus? eventBus = null,
        CiMonitorService? ciMonitor = null)
    {
        _repoManager = repoManager ?? throw new ArgumentNullException(nameof(repoManager));
        _goalManager = goalManager;
        _pipelineManager = pipelineManager;
        _taskQueue = taskQueue;
        _workerGateway = workerGateway;
        _brain = brain;
        _logger = logger;
        _config = config;
        _clarificationRouter = clarificationRouter;
        _clarificationQueue = clarificationQueue;
        _startupDelay = startupDelay ?? TimeSpan.FromSeconds(10);
        _progressLog = progressLog;
        _knowledgeGraph = knowledgeGraph;
        _configRepo = configRepo;
        _goalStore = goalStore;
        _dashboardNotifier = dashboardNotifier;
        _goalReadyNotifier = goalReadyNotifier;
        _clarificationHandler = new ClarificationHandler(brain, clarificationRouter, clarificationQueue, logger);

        _lifecycleService = new GoalLifecycleService(
            goalManager, logger, metricsTracker, agentsManager, configRepo, brain, dashboardNotifier, eventBus, ciMonitor);

        _maintenance = new DispatcherMaintenance(
            pipelineManager, goalManager, taskQueue, workerGateway, brain,
            agentsManager, configRepo, _redispatchQueue, logger,
            knowledgeGraph,
            goalStore: goalStore,
            repoManager: repoManager,
            config: config,
            goalReadyNotifier: goalReadyNotifier);

        _taskDispatchService = new TaskDispatchService(
            _taskQueue, _workerGateway, _taskBuilder, _config,
            NullLogger<TaskDispatchService>.Instance, _pipelineManager, _lifecycleService, _maintenance);

        _pipelineDriver = new PipelineDriver(
            brain: brain,
            lifecycleService: _lifecycleService,
            goalManager: goalManager,
            repoManager: repoManager,
            improvementAnalyzer: improvementAnalyzer,
            agentsManager: agentsManager,
            metricsTracker: metricsTracker,
            dispatchToRole: DispatchToRole,
            resolvePrompt: ResolvePromptAsync,
            resolvePlan: ResolvePlanAsync,
            resolveRepositories: ResolveRepositories,
            syncAgents: ct => _maintenance.SyncAgentsFromConfigRepoAsync(ct),
            generateMergeCommitMessage: GenerateMergeCommitMessageAsync,
            logger: logger,
            knowledgeGraph: _knowledgeGraph,
            configRepo: _configRepo);

        _taskCompletionService = new TaskCompletionService(
            _pipelineManager, _brain, _pipelineDriver, _lifecycleService,
            _dashboardNotifier, _logger);

        _goalDispatchService = new GoalDispatchService(
            _goalManager, _pipelineManager, _brain, _config,
            _taskDispatchService, _clarificationHandler, _knowledgeGraph,
            _goalStore, _configRepo, _dashboardNotifier, _logger,
            eventBus);

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

            await _lifecycleService.MarkGoalFailedAsync(pipeline, "Cancelled by user", ct);
            _pipelineManager.RemovePipeline(goalId);
            _logger.LogInformation("Goal {GoalId} cancelled (was InProgress, phase={Phase})", goalId, pipeline.Phase);
            if (_brain is not null)
                await _brain.DeleteGoalSessionAsync(goalId);
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
        _dashboardNotifier?.NotifyStateChanged();
        _logger.LogInformation("Goal {GoalId} cancelled (was {Status})", goalId, goal.Status);
        if (_brain is not null)
            await _brain.DeleteGoalSessionAsync(goalId);
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
    /// Upper bound for a single per-repository branch observation during a branch-backed resume
    /// (variant A). The same value bounds BOTH the token handed to the branch lister
    /// (<see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>) and the defensive
    /// caller-side <c>WaitAsync</c> bound. The observation runs inside the global resume lock,
    /// so the worst-case added lock latency is <c>repositories × ResumeTimeout</c>; the 10 second
    /// default keeps that bounded in practice. Settable for tests only.
    /// </summary>
    internal TimeSpan ResumeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Test seam replacing the real remote-branch listing
    /// (<see cref="IBrainRepoManager.ListRemoteBranchesAsync"/>) used by the resume branch
    /// observation. <c>null</c> (the production value) means the real repo manager is used.
    /// </summary>
    internal Func<string, CancellationToken, Task<List<string>>>? BranchListerForTest { get; set; }

    /// <summary>Result of observing one repository for the feature branch.</summary>
    private enum ResumeBranchState
    {
        /// <summary>The branch was found (per repo), or found in every repo (aggregate).</summary>
        Present,
        /// <summary>The branch was absent (per repo), or absent in at least one repo (aggregate).</summary>
        Absent,
        /// <summary>The branch state could not be determined.</summary>
        Unknown,
    }

    /// <summary>Phases that represent worker-facing work, used to resolve the failed phase.</summary>
    private static readonly GoalPhase[] WorkerFacingPhases =
    [
        GoalPhase.Coding,
        GoalPhase.Testing,
        GoalPhase.DocWriting,
        GoalPhase.Review,
        GoalPhase.Improve,
        GoalPhase.Merging,
    ];

    /// <summary>The literal rendered whenever a resume metadata value cannot be determined.</summary>
    private const string ResumeUnknown = "unknown";

    /// <summary>
    /// Determines whether a goal failed specifically due to iteration-budget exhaustion,
    /// making it eligible for the branchless (variant B) resumption via
    /// <see cref="ResumeGoalAsync"/>.
    /// Matches the failure reasons produced by <see cref="PipelineDriver"/>:
    /// "Exceeded max iterations" and "Exceeded max iterations during merge conflict resolution".
    /// </summary>
    private static bool IsIterationExhaustionFailure(Goal goal)
    {
        if (goal.Status != GoalStatus.Failed)
            return false;
        if (string.IsNullOrEmpty(goal.FailureReason))
            return false;
        var reason = goal.FailureReason;
        return reason.Contains("Exceeded max iterations", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("max iterations", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The cancellation predicate: a goal is cancellation-failed iff its
    /// <see cref="Goal.FailureReason"/> EQUALS "Cancelled by user" under
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>. Equality — never <c>Contains</c>: a
    /// reason such as "Cancelled by user (test)" is a different failure and stays resumable.
    /// Cancellation-failed goals are never resumable (the snapshot-removal contract owns them).
    /// </summary>
    private static bool IsCancellationFailure(Goal goal) =>
        string.Equals(goal.FailureReason, "Cancelled by user", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Goal-level resume eligibility: a Failed goal that was not cancelled by a user.
    /// Deliberately broader than <see cref="IsIterationExhaustionFailure"/> so that any
    /// non-cancellation failure reaches the in-lock pipeline load, where
    /// <see cref="GoalPipeline.CoderBranch"/> decides between the branch-backed restart
    /// (variant A) and the branchless exhaustion resume (variant B).
    /// </summary>
    private static bool IsResumeCandidateGoal(Goal goal) =>
        goal.Status == GoalStatus.Failed && !IsCancellationFailure(goal);

    /// <summary>
    /// Collapses a failure reason to a single, bounded, control-character-free line for logging
    /// and for the failure-informed planning context. The exact algorithm: every CR and LF becomes
    /// a single space; every other control character (below 0x20 or 0x7F) is removed; consecutive
    /// spaces are collapsed; the result is trimmed; anything longer than 300 characters is
    /// truncated to 297 characters followed by "...". A null, whitespace-only, or
    /// control-character-only input renders as the literal "unknown".
    /// </summary>
    /// <param name="value">The raw failure reason.</param>
    /// <returns>The sanitized single-line rendering, never null or empty.</returns>
    internal static string SanitizedSingleLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return ResumeUnknown;

        var stripped = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\r' or '\n')
            {
                stripped.Append(' ');
                continue;
            }

            if (c < 0x20 || c == 0x7F)
                continue;

            stripped.Append(c);
        }

        var collapsed = new StringBuilder(stripped.Length);
        var previousWasSpace = false;
        for (var i = 0; i < stripped.Length; i++)
        {
            var c = stripped[i];
            if (c == ' ')
            {
                if (previousWasSpace)
                    continue;
                previousWasSpace = true;
            }
            else
            {
                previousWasSpace = false;
            }

            collapsed.Append(c);
        }

        var trimmed = collapsed.ToString().Trim();
        if (trimmed.Length == 0)
            return ResumeUnknown;

        return trimmed.Length > 300 ? string.Concat(trimmed.AsSpan(0, 297), "...") : trimmed;
    }

    /// <summary>
    /// Resolves the phase the goal failed in by scanning <see cref="GoalPipeline.PhaseLog"/>
    /// BACKWARDS for the first entry naming a worker-facing phase (Coding, Testing, DocWriting,
    /// Review, Improve, Merging). An empty log — or a log containing only terminal entries —
    /// resolves to the literal "unknown".
    /// </summary>
    /// <param name="pipeline">The failed pipeline.</param>
    /// <returns>The failed phase name, or "unknown".</returns>
    internal static string ResolveFailedPhase(GoalPipeline pipeline)
    {
        for (var i = pipeline.PhaseLog.Count - 1; i >= 0; i--)
        {
            var name = pipeline.PhaseLog[i].Name;
            if (Array.IndexOf(WorkerFacingPhases, name) >= 0)
                return name.ToString();
        }

        return ResumeUnknown;
    }

    /// <summary>Renders a branch-observation result for the resume log line.</summary>
    private static string RenderRepoResult(ResumeBranchState state) => state switch
    {
        ResumeBranchState.Present => "true",
        ResumeBranchState.Absent => "false",
        ResumeBranchState.Unknown => ResumeUnknown,
        _ => throw new InvalidOperationException($"Unhandled resume branch state '{state}'"),
    };

    /// <summary>Renders an aggregate branch state for the resume log line.</summary>
    private static string RenderBranchState(ResumeBranchState state) => state switch
    {
        ResumeBranchState.Present => "present",
        ResumeBranchState.Absent => "absent",
        ResumeBranchState.Unknown => ResumeUnknown,
        _ => throw new InvalidOperationException($"Unhandled resume branch state '{state}'"),
    };

    /// <summary>
    /// Observes whether the feature branch still exists, per repository, sequentially.
    /// Every repository gets a FRESH <see cref="CancellationTokenSource"/> cancelled after
    /// <see cref="ResumeTimeout"/>, whose token is handed to the lister so a well-behaved
    /// listing aborts and releases its resources at the deadline. A defensive
    /// <c>WaitAsync(ResumeTimeout)</c> caller-side bound covers a cancellation-ignoring
    /// implementation; when it fires, the outliving lister task is observed by a continuation
    /// (which also owns the CTS disposal) so its eventual fault is logged, never unobserved.
    /// Any timeout or throw yields <see cref="ResumeBranchState.Unknown"/> for that repository
    /// and the loop continues.
    /// </summary>
    /// <param name="pipeline">The pipeline whose <see cref="GoalPipeline.CoderBranch"/> is looked up.</param>
    /// <returns>The aggregate state plus the per-repository results in repository order.</returns>
    private async Task<(ResumeBranchState Aggregate, List<(string Repo, ResumeBranchState Result)> PerRepo)>
        ObserveBranchStateAsync(GoalPipeline pipeline)
    {
        var branch = pipeline.CoderBranch!;
        var perRepo = new List<(string Repo, ResumeBranchState Result)>();

        foreach (var repoName in pipeline.Goal.RepositoryNames)
            perRepo.Add((repoName, await ObserveRepositoryAsync(repoName, branch)));

        ResumeBranchState aggregate;
        if (perRepo.Count == 0)
            aggregate = ResumeBranchState.Unknown;
        else if (perRepo.Exists(r => r.Result == ResumeBranchState.Absent))
            aggregate = ResumeBranchState.Absent;
        else if (perRepo.TrueForAll(r => r.Result == ResumeBranchState.Present))
            aggregate = ResumeBranchState.Present;
        else
            aggregate = ResumeBranchState.Unknown;

        return (aggregate, perRepo);
    }

    /// <summary>Observes one repository for the feature branch under the per-repo deadline.</summary>
    private async Task<ResumeBranchState> ObserveRepositoryAsync(string repoName, string branch)
    {
        var lister = BranchListerForTest ?? _repoManager.ListRemoteBranchesAsync;
        var cts = new CancellationTokenSource();
        var ctsOwnershipTransferred = false;
        try
        {
            cts.CancelAfter(ResumeTimeout);

            Task<List<string>> listing;
            try
            {
                listing = lister(repoName, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Resume branch observation failed for repository '{Repo}' — treating branch state as unknown", repoName);
                return ResumeBranchState.Unknown;
            }

            List<string> branches;
            try
            {
                branches = await listing.WaitAsync(ResumeTimeout);
            }
            catch (TimeoutException)
            {
                // The defensive caller-side bound fired: the listing may still be running.
                // Never leave it unobserved — a continuation logs its eventual fault and
                // disposes the CTS once the token can no longer be used.
                ObserveOutlivingListing(listing, repoName, cts);
                ctsOwnershipTransferred = true;
                _logger.LogWarning(
                    "Resume branch observation for repository '{Repo}' exceeded {Timeout} — treating branch state as unknown",
                    repoName, ResumeTimeout);
                return ResumeBranchState.Unknown;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Resume branch observation failed for repository '{Repo}' — treating branch state as unknown", repoName);
                return ResumeBranchState.Unknown;
            }

            return branches.Exists(b => string.Equals(b, branch, StringComparison.OrdinalIgnoreCase))
                ? ResumeBranchState.Present
                : ResumeBranchState.Absent;
        }
        finally
        {
            if (!ctsOwnershipTransferred)
                cts.Dispose();
        }
    }

    /// <summary>
    /// Attaches a continuation to a branch listing that outlived the observation deadline so its
    /// eventual fault is logged and the per-repo <see cref="CancellationTokenSource"/> is disposed
    /// only once the listing can no longer touch its token.
    /// </summary>
    private void ObserveOutlivingListing(Task<List<string>> listing, string repoName, CancellationTokenSource cts)
    {
        _ = listing.ContinueWith(
            t =>
            {
                if (t.IsFaulted)
                {
                    _logger.LogWarning(t.Exception,
                        "Branch listing for repository '{Repo}' faulted after the resume observation deadline", repoName);
                }

                cts.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Builds the failure-informed planning context for a branch-backed (variant A) resume:
    /// the captured failure reason, the failed phase, and the honest, conditional wording about
    /// what the branch observation could establish.
    /// </summary>
    private static string BuildResumeContext(
        string goalId, string sanitizedReason, string failedPhase, ResumeBranchState branchState)
    {
        var branchWording = branchState switch
        {
            ResumeBranchState.Present =>
                $"The feature branch `copilothive/{goalId}` carries the prior iterations' merged work. "
                + "Checkout the existing branch; do NOT recreate it from scratch.",
            ResumeBranchState.Absent =>
                "The feature branch appears absent — checkout may fall back to re-creating it from the base; "
                + "prior branch work may be lost.",
            ResumeBranchState.Unknown =>
                "The branch state could not be verified; checkout may fall back to re-creating it from the base.",
            _ => throw new InvalidOperationException($"Unhandled resume branch state '{branchState}'"),
        };

        return $"""
            This goal is being restarted after a failure. Plan a continuation, starting with Coding.
            Previous failure reason: {sanitizedReason}
            Failed phase: {failedPhase}
            {branchWording}
            """;
    }

    /// <summary>
    /// Resumes a failed goal. Two variants:
    /// <list type="bullet">
    ///   <item><b>Variant A (branch-backed restart)</b> — the pipeline has a
    ///   <see cref="GoalPipeline.CoderBranch"/>: the branch is observed, a failure-informed
    ///   planning context is built, the plan must start with Coding, and dispatch checks the
    ///   existing branch out (non-destructive).</item>
    ///   <item><b>Variant B (branchless exhaustion resume)</b> — no coder branch and an
    ///   iteration-exhaustion failure: the historical behaviour, unchanged.</item>
    /// </list>
    /// Goals failed by user cancellation are never resumable. Serialized via a global lock.
    /// </summary>
    /// <param name="goalId">The goal to resume.</param>
    /// <param name="additionalIterations">Number of additional iterations to grant (1-1000).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the goal was resumed; false if the goal is not eligible or the goal store is unavailable.</returns>
    public async Task<bool> ResumeGoalAsync(string goalId, int additionalIterations, CancellationToken ct = default)
    {
        // Resolve goal store — mandatory for resume
        var goalStore = _goalStore;
        if (goalStore is null)
            return false;

        // Check goal-level eligibility BEFORE acquiring lock. Deliberately broad: the variant
        // is decided in-lock, once the pipeline (and its CoderBranch) is loaded.
        var goal = await goalStore.GetGoalAsync(goalId, ct);
        if (goal is null || !IsResumeCandidateGoal(goal))
            return false;

        var lockObj = _resumeLock;
        await lockObj.WaitAsync(ct);
        try
        {
            // Re-check goal-level eligibility inside lock (could have changed)
            goal = await goalStore.GetGoalAsync(goalId, ct);
            if (goal is null || !IsResumeCandidateGoal(goal))
                return false;

            // Load pipeline
            var pipeline = _pipelineManager.GetByGoalId(goalId);
            if (pipeline is null)
            {
                pipeline = _pipelineManager.RestorePipeline(goalId);
                if (pipeline is null)
                    return false;
            }

            // Require pipeline in Failed phase
            if (pipeline.Phase != GoalPhase.Failed)
                return false;

            // ── Variant selection (no mutation may precede this) ─────────────────
            var isBranchBacked = pipeline.CoderBranch is not null;
            if (isBranchBacked)
            {
                // Branch-name invariant: ORDINAL, case-SENSITIVE. Git branch names are
                // case-sensitive, so a case-only mismatch signals a corrupted snapshot and
                // must surface as a rejection, never as a silent recreate.
                var canonicalBranch = $"copilothive/{goalId}";
                if (!string.Equals(pipeline.CoderBranch, canonicalBranch, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "Refusing to resume goal '{GoalId}': coder branch '{CoderBranch}' does not match the canonical branch '{Canonical}'",
                        goalId, pipeline.CoderBranch, canonicalBranch);
                    return false;
                }
            }
            else if (!IsIterationExhaustionFailure(goal))
            {
                // Branchless resume is only defined for iteration exhaustion.
                return false;
            }

            // Capture the failure metadata BEFORE any mutation (ExtendIterations is the first).
            var sanitizedReason = SanitizedSingleLine(goal.FailureReason);
            var failedPhase = ResolveFailedPhase(pipeline);

            // Extend budget
            pipeline.ExtendIterations(additionalIterations);

            // Consume one iteration for the resumed iteration
            if (!pipeline.IterationBudget.TryConsume())
                return false; // shouldn't happen after TopUp

            // Capture stale task ID before clearing
            var staleTaskId = pipeline.ActiveTaskId;
            if (staleTaskId is not null)
            {
                _pipelineManager.UnregisterTask(staleTaskId);
                pipeline.ClearActiveTask();
            }

            // Clear terminal state
            pipeline.ClearCompletedAt();
            pipeline.StateMachine.ResetToPlanning();
            pipeline.AdvanceTo(GoalPhase.Planning);
            pipeline.SetPlan(IterationPlan.Default());
            pipeline.Metrics.ResetForNewIteration(pipeline.Iteration);

            // Update goal status FIRST (DB is source of truth)
            // If this throws, goal stays Failed — no pipeline mutation has been persisted
            goal.Status = GoalStatus.InProgress;
            goal.FailureReason = null;
            goal.CompletedAt = null;
            await goalStore.UpdateGoalAsync(goal, ct);

            // Persist pipeline at recovery boundary
            _pipelineManager.PersistFull(pipeline);

            // Re-register with Brain and fork session (best-effort)
            try
            {
                (_brain as DistributedBrain)?.RegisterActivePipeline(pipeline);
                if (_brain is not null)
                    await _brain.ForkSessionForGoalAsync(goalId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to re-register/fork Brain session for resumed goal '{GoalId}'", goalId);
            }

            // Plan a new iteration — a planning failure fails the goal (no default plan substitution)
            // Variant A: observe the surviving feature branch and build a failure-informed context.
            // Variant B: the historical branchless resume — a null context, no observation.
            string? planningContext = null;
            if (isBranchBacked)
            {
                var (branchState, perRepo) = await ObserveBranchStateAsync(pipeline);
                planningContext = BuildResumeContext(goalId, sanitizedReason, failedPhase, branchState);

                var repoRendering = string.Join(", ", perRepo.Select(r => $"{r.Repo}:{RenderRepoResult(r.Result)}"));
                _logger.LogInformation(
                    "ResumeRestart for goal {GoalId}: failed-phase={FailedPhase}, failure-reason={FailureReason}, branch={Branch}, branch-state={BranchState}, repos=[{Repos}]",
                    goalId, failedPhase, sanitizedReason, pipeline.CoderBranch, RenderBranchState(branchState), repoRendering);
            }

            PlanResult planResult;
            try
            {
                // The CALLER's token governs planning so the cancellation distinction below is
                // real: only an OCE carrying this token means "the caller is shutting down".
                planResult = await ResolvePlanAsync(pipeline, planningContext, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The CALLER's token was cancelled — service shutdown, not a planning failure.
                // Propagate so the goal is NOT marked Failed: it stays InProgress and the next
                // dispatch cycle picks it up again.
                _logger.LogWarning("Resume of goal '{GoalId}' was cancelled by the caller", goalId);
                throw;
            }
            catch (OperationCanceledException)
            {
                // Self-cancellation from inside the planning call (e.g. a Brain-side timeout),
                // NOT the caller's token. The goal is already persisted as InProgress/Planning,
                // so fail it explicitly rather than stranding it.
                _logger.LogWarning("Planning was cancelled for resumed goal '{GoalId}'", goalId);
                await FailResumedGoalAsync(pipeline, "Planning failed: planning was cancelled");
                return true;
            }
            catch (Exception ex)
            {
                // The goal is already persisted as InProgress/Planning — a throw here would
                // strand it. Fail the goal explicitly instead.
                _logger.LogError(ex, "Brain planning threw for resumed goal '{GoalId}'", goalId);
                await FailResumedGoalAsync(pipeline, $"Planning failed: {ex.Message}");
                return true;
            }

            if (planResult.IsFailed)
            {
                _logger.LogWarning("Brain failed to plan resumed iteration for {GoalId}: {Reason}", goalId, planResult.FailureReason);
                await FailResumedGoalAsync(pipeline, planResult.FailureReason!);
                return true;
            }

            var validatedPlan = planResult.Plan!;

            // Variant A restarts the surviving branch work — the continuation MUST start with
            // Coding. Any other shape is rejected rather than dispatched to the wrong role.
            if (isBranchBacked && validatedPlan.Phases.FirstOrDefault() != GoalPhase.Coding)
            {
                _logger.LogWarning(
                    "Resumed plan for goal {GoalId} does not start with Coding — failing the goal", goalId);

                await FailResumedGoalAsync(pipeline, "Resume plan must start with Coding");
                return true;
            }

            pipeline.SetPlan(validatedPlan);
            pipeline.StateMachine.StartIteration(validatedPlan.Phases);
            var firstPhase = validatedPlan.Phases[0];
            pipeline.AdvanceTo(firstPhase);
            pipeline.PhaseLog.Add(PhaseResult.Create(firstPhase, pipeline.Iteration, 1));

            // Craft prompt (best-effort)
            string prompt;
            try
            {
                prompt = _brain is not null
                    ? await ResolvePromptAsync(pipeline, firstPhase, null, CancellationToken.None)
                    : BuildCoderPrompt(pipeline.Goal);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Brain failed to craft prompt for resumed {GoalId} — using fallback", goalId);
                prompt = BuildCoderPrompt(pipeline.Goal);
            }

            // Dispatch (best-effort — if this fails, goal is InProgress and dispatch loop handles it)
            try
            {
                await DispatchToRole(pipeline, firstPhase.ToWorkerRole(), prompt, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dispatch resumed goal '{GoalId}' — enqueuing for redispatch", goalId);
                // CRITICAL: Clear ActiveTaskId before enqueuing — DrainRedispatchQueueAsync
                // skips pipelines with non-null ActiveTaskId.
                pipeline.ClearActiveTask();
                _redispatchQueue.Enqueue(goalId);
            }

            _pipelineManager.PersistFull(pipeline);
            return true;
        }
        finally
        {
            lockObj.Release();
        }
    }

    /// <summary>
    /// Fails a resumed goal whose iteration could not be planned. Synchronizes the state machine
    /// with the pipeline phase, marks the goal Failed in the store and durably persists the
    /// terminal pipeline state.
    /// </summary>
    /// <remarks>
    /// <see cref="GoalLifecycleService.MarkGoalFailedAsync"/> performs the
    /// <c>AdvanceTo(GoalPhase.Failed)</c> transition itself, so this method must NOT advance the
    /// pipeline as well — a second advance would rewrite <c>CompletedAt</c> after the lifecycle
    /// metadata was already written, leaving the goal and the persisted pipeline with mismatched
    /// timestamps. <c>StateMachine.Fail()</c> is a separate concern (<c>AdvanceTo</c> does not
    /// touch the state machine) and is still required to keep the two in sync.
    /// </remarks>
    /// <param name="pipeline">The resumed pipeline.</param>
    /// <param name="reason">Why planning failed.</param>
    private async Task FailResumedGoalAsync(GoalPipeline pipeline, string reason)
    {
        // AdvanceTo does not touch the state machine, so fail it explicitly to keep both in sync.
        pipeline.StateMachine.Fail();
        try
        {
            await _lifecycleService.MarkGoalFailedAsync(pipeline, reason, CancellationToken.None);
        }
        finally
        {
            // Durably persist the terminal pipeline state even if finalization threw.
            // No AdvanceTo here — MarkGoalFailedAsync owns that transition.
            _pipelineManager.PersistFull(pipeline);
        }
    }

    /// <summary>
    /// Clears all dispatcher runtime state for a goal that is being retried (Failed→Draft).
    /// Removes the stale pipeline from the <see cref="GoalPipelineManager"/> so the
    /// dispatcher does not see an active pipeline blocking new goal dispatch.
    /// </summary>
    /// <param name="goalId">The goal being retried.</param>
    public void ClearGoalRetryState(string goalId)
    {
        if (_brain is not null)
        {
            _ = Task.Run(async () =>
            {
                try { await _brain.DeleteGoalSessionAsync(goalId); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete goal session for {GoalId}", goalId); }
            });
        }
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
    /// with the answer as additional context. Failures are returned as
    /// <see cref="PlanResult.Failed(string)"/> — never a substituted default plan.
    /// Exposed as <c>internal</c> for unit testing via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal Task<PlanResult> ResolvePlanAsync(
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
                if (DateTime.UtcNow - _maintenance.LastAgentsSync > AgentsSyncInterval)
                {
                    await SyncAgentsFromConfigRepoAsync(stoppingToken);
                }

                // Periodic branch cleanup for completed goals
                if (DateTime.UtcNow - _lastBranchCleanup > BranchCleanupInterval)
                {
                    await _maintenance.CleanupMergedBranchesAsync(stoppingToken);
                    _lastBranchCleanup = DateTime.UtcNow;
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

            if (_goalReadyNotifier is not null)
                await _goalReadyNotifier.WaitForSignalAsync(PollInterval, stoppingToken);
            else
                await Task.Delay(PollInterval, stoppingToken);
        }

        _logger.LogInformation("GoalDispatcher stopped");
    }

    /// <summary>
    /// Called by HiveOrchestratorService when a worker completes a task.
    /// Drives the pipeline to its next phase using the Brain.
    /// </summary>
    public Task HandleTaskCompletionAsync(TaskResult result, CancellationToken ct = default)
        => _taskCompletionService.HandleTaskCompletionAsync(result, ct);

    // ── Forwarding wrappers for instance methods (tests call via reflection) ──

    private Task MarkGoalCompleted(GoalPipeline pipeline, CancellationToken ct)
        => _lifecycleService.MarkGoalCompletedAsync(pipeline, ct);

    private Task MarkGoalFailed(GoalPipeline pipeline, string reason, CancellationToken ct)
        => _lifecycleService.MarkGoalFailedAsync(pipeline, reason, ct);

    private Task HandleNewIterationAsync(GoalPipeline pipeline, string verdict, CancellationToken ct)
        => _pipelineDriver.HandleNewIterationAsync(pipeline, verdict, ct);

    private Task HandleMergeFailureAsync(GoalPipeline pipeline, string errorMessage, CancellationToken ct)
        => _pipelineDriver.HandleMergeFailureAsync(pipeline, errorMessage, ct);

    private Task DispatchToRole(GoalPipeline pipeline, WorkerRole role, string? prompt, CancellationToken ct)
        => _taskDispatchService.DispatchToRole(pipeline, role, prompt, ct);

    private Task SendAgentsMdToWorkerAsync(ConnectedWorker worker, WorkerRole role, CancellationToken ct)
        => _maintenance.SendAgentsMdToWorkerAsync(worker, role, ct);

    /// <summary>
    /// Sends an UpdateAgents message to all connected workers whose role matches the given role string.
    /// Best-effort: failures are logged but do not block the pipeline.
    /// </summary>
    private Task BroadcastAgentsUpdateAsync(WorkerRole role, string content, CancellationToken ct)
        => _maintenance.BroadcastAgentsUpdateAsync(role, content, ct);

    /// <summary>
    /// Pulls the latest config repo and broadcasts any AGENTS.md changes to connected workers.
    /// Best-effort: failures are logged but do not block the main dispatch loop.
    /// </summary>
    private Task SyncAgentsFromConfigRepoAsync(CancellationToken ct)
        => _maintenance.SyncAgentsFromConfigRepoAsync(ct);

    /// <summary>
    /// Restore active pipelines from the persistence store on startup.
    /// Re-primes Brain sessions and restores pipelines in the GoalPipelineManager.
    /// </summary>
    private Task RestoreActivePipelinesAsync(CancellationToken ct)
        => _maintenance.RestoreActivePipelinesAsync(ct);

    private Task CleanupOrphanedGoalSessionsAsync(CancellationToken ct)
        => _maintenance.CleanupOrphanedGoalSessionsAsync(ct);

    /// <summary>
    /// Drains the re-dispatch queue and dispatches the current phase for each
    /// queued pipeline. Called from the main poll loop — all dispatching happens here,
    /// avoiding race conditions from polling-based orphan detection.
    /// </summary>
    private Task DrainRedispatchQueueAsync(CancellationToken ct)
        => _goalDispatchService.DrainRedispatchQueueAsync(_redispatchQueue, ct);

    private Task DispatchNextGoalAsync(CancellationToken ct)
        => _goalDispatchService.DispatchNextGoalAsync(ct);

    internal List<TargetRepository> ResolveRepositories(Goal goal)
        => _taskDispatchService.ResolveRepositories(goal);

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
    /// Generates a commit message by asking the Brain for a concise summary first,
    /// falling back to <see cref="PipelineHelpers.BuildSquashCommitMessage"/> when the Brain is unavailable
    /// or returns null. The "Goal:" prefix is always preserved.
    /// </summary>
    internal async Task<string> GenerateMergeCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct)
    {
        if (_brain is null)
            return PipelineHelpers.BuildSquashCommitMessage(pipeline.GoalId, pipeline.Description);

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

        return PipelineHelpers.BuildSquashCommitMessage(pipeline.GoalId, pipeline.Description);
    }

}
