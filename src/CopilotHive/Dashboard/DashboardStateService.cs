using System.Reflection;
using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;

namespace CopilotHive.Dashboard;

/// <summary>
/// Aggregates live state from WorkerPool, GoalPipelineManager, and SqliteGoalStore
/// into a snapshot that Blazor components can bind to. Polls every few seconds
/// and fires <see cref="OnStateChanged"/> to trigger UI re-renders.
/// </summary>
public sealed class DashboardStateService : IDisposable
{
    private readonly WorkerPool _workerPool;
    private readonly GoalPipelineManager _pipelineManager;
    private readonly GoalManager _goalManager;
    private readonly IGoalStore? _goalStore;
    private readonly DashboardLogSink _logSink;
    private readonly ProgressLog _progressLog;
    private readonly IDistributedBrain? _brain;
    private readonly Composer? _composer;
    private readonly HiveConfigFile? _config;
    private readonly Timer _timer;
    private readonly DateTime _startTime = DateTime.UtcNow;
    private readonly string _version =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";
    private readonly string? _sharpCoderVersion = typeof(SharpCoder.CodingAgent).Assembly.GetName().Version?.ToString();
    private List<Goal> _cachedPendingGoals = [];

    /// <summary>Fired when state has been polled and components should re-render.</summary>
    public event Action? OnStateChanged;

    /// <summary>Creates the service with required dependencies.</summary>
    public DashboardStateService(
        WorkerPool workerPool,
        GoalPipelineManager pipelineManager,
        GoalManager goalManager,
        DashboardLogSink logSink,
        ProgressLog progressLog,
        IDistributedBrain? brain = null,
        Composer? composer = null,
        HiveConfigFile? config = null,
        IGoalStore? goalStore = null)
    {
        _workerPool = workerPool;
        _pipelineManager = pipelineManager;
        _goalManager = goalManager;
        _logSink = logSink;
        _progressLog = progressLog;
        _brain = brain;
        _composer = composer;
        _config = config;
        _goalStore = goalStore;
        _timer = new Timer(_ => PollAndNotify(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
    }

    private void PollAndNotify()
    {
        // Refresh cached pending goals on the threadpool (safe to call async here)
        try
        {
            var goals = new List<Goal>();
            foreach (var source in _goalManager.Sources)
            {
                var pending = source.GetPendingGoalsAsync().GetAwaiter().GetResult();
                goals.AddRange(pending);
            }
            _cachedPendingGoals = goals;
        }
        catch
        {
            // Don't crash the timer on transient failures
        }

        OnStateChanged?.Invoke();
    }

    /// <summary>Creates a snapshot of current system state.</summary>
    public DashboardSnapshot GetSnapshot()
    {
        var workers = _workerPool.GetAllWorkers();
        var pipelines = _pipelineManager.GetAllPipelines();

        // Collect goals from cached pending goals (refreshed by timer on threadpool)
        var goalsById = new Dictionary<string, Goal>();

        foreach (var g in _cachedPendingGoals)
            goalsById.TryAdd(g.Id, g);

        // Add all goals from SQLite store (includes all statuses)
        if (_goalStore is not null)
        {
            var allGoals = _goalStore.GetAllGoalsAsync().GetAwaiter().GetResult();
            foreach (var g in allGoals)
                goalsById.TryAdd(g.Id, g);
        }

        // Merge pipeline goals and derive status from pipeline phase
        foreach (var p in pipelines)
        {
            goalsById.TryAdd(p.GoalId, p.Goal);

            goalsById[p.GoalId].Status = p.Phase switch
            {
                GoalPhase.Done => GoalStatus.Completed,
                GoalPhase.Failed => GoalStatus.Failed,
                _ => GoalStatus.InProgress,
            };
        }

        var goals = goalsById.Values.ToList();

        return new DashboardSnapshot
        {
            Goals = goals,
            Workers = workers.Select(w => new WorkerInfo
            {
                Id = w.Id,
                Role = w.Role.ToString(),
                IsBusy = w.IsBusy,
                CurrentTaskId = w.CurrentTaskId,
                LastHeartbeat = w.LastHeartbeat,
                ConnectedAt = w.ConnectedAt,
                CurrentModel = w.CurrentModel,
            }).ToList(),
            Pipelines = pipelines.Select(p => new PipelineInfo
            {
                GoalId = p.GoalId,
                Description = p.Description,
                Phase = p.Phase.ToString(),
                Phases = p.Plan?.Phases.Select(ph => ph.ToString()).ToList() ?? [],
                Iteration = p.Iteration,
                ActiveTaskId = p.ActiveTaskId,
                CreatedAt = p.CreatedAt,
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
        };
    }

    /// <summary>Returns recent log entries from the circular buffer.</summary>
    public IReadOnlyList<LogEntry> GetRecentLogs(int count = 500) => _logSink.GetRecent(count);

    /// <summary>Returns current Brain statistics, or null if Brain is not configured.</summary>
    public BrainStats? GetBrainStats() => _brain?.GetStats();

    /// <summary>Returns current Composer statistics, or null if Composer is not configured.</summary>
    public BrainStats? GetComposerStats() => _composer?.GetStats();

    /// <summary>
    /// Resets the Brain's conversation session, clearing all history and deleting the session file.
    /// Does nothing if the Brain is not configured.
    /// </summary>
    public async Task ResetBrainSessionAsync(CancellationToken ct = default)
    {
        if (_brain is not null)
            await _brain.ResetSessionAsync(ct);
    }

    /// <summary>Returns recent worker progress reports.</summary>
    public IReadOnlyList<ProgressEntry> GetRecentProgress(int count = 50) => _progressLog.GetRecent(count);

    /// <summary>Returns recent progress reports for a specific worker.</summary>
    public IReadOnlyList<ProgressEntry> GetProgressForWorker(string workerId, int count = 100) =>
        _progressLog.GetRecent(500)
            .Where(e => e.WorkerId == workerId)
            .TakeLast(count)
            .ToList();

    /// <summary>Returns recent progress reports for a specific goal.</summary>
    public IReadOnlyList<ProgressEntry> GetProgressForGoal(string goalId, int count = 100) =>
        _progressLog.GetRecent(500)
            .Where(e => e.GoalId == goalId)
            .TakeLast(count)
            .ToList();

    /// <summary>Builds a rich detail view for a specific goal, including per-iteration phase info.</summary>
    public GoalDetailInfo? GetGoalDetail(string goalId)
    {
        var snap = GetSnapshot();
        var goal = snap.Goals.FirstOrDefault(g => g.Id == goalId);
        if (goal is null)
            return null;

        // GetSnapshot uses GetAllGoalsAsync which does NOT populate IterationSummaries.
        // For the detail view we need the full goal with summaries loaded from the store.
        if (goal.IterationSummaries.Count == 0 && _goalStore is not null)
        {
            var fullGoal = _goalStore.GetGoalAsync(goalId).GetAwaiter().GetResult();
            if (fullGoal is not null)
                goal = fullGoal;
        }

        var pipeline = _pipelineManager.GetByGoalId(goalId);
        var progress = GetProgressForGoal(goalId);
        var iterations = new List<IterationViewInfo>();

        // Build views for completed iterations from IterationSummaries
        // Merge persisted summaries (from goal source) with in-memory summaries (from pipeline)
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
            var summaryCraftPrompts = ExtractCraftPrompts(summaryConversation, summary.Iteration);
            var (summaryPlanningPrompt, summaryPlanningResponse) = ExtractPlanningPrompts(summaryConversation, summary.Iteration);

            var phases = new List<PhaseViewInfo>
            {
                new PhaseViewInfo
                {
                    Name = "Planning",
                    RoleName = "",
                    Status = "completed",
                },
            };

            // Group persisted clarifications by phase name for this iteration
            var summaryClarificationsByPhase = summary.Clarifications
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
                        AnsweredBy: c.AnsweredBy)).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var pr in summary.Phases)
            {
                var roleName = PhaseNameToRoleName(pr.Name);

                // Prefer PhaseResult.WorkerOutput (persisted) over live pipeline PhaseOutputs.
                // This ensures completed goals (no pipeline) still display output correctly.
                string? workerOutput = pr.WorkerOutput;
                if (string.IsNullOrEmpty(workerOutput) && !string.IsNullOrEmpty(roleName))
                    pipeline?.PhaseOutputs.TryGetValue($"{roleName}-{summary.Iteration}", out workerOutput);

                var isTestPhase = pr.Name == "Testing";
                var isReviewPhase = pr.Name == "Review";

                summaryClarificationsByPhase.TryGetValue(pr.Name, out var summaryClarifications);
                summaryCraftPrompts.TryGetValue(roleName, out var summaryPhasePrompts);
                phases.Add(new PhaseViewInfo
                {
                    Name = pr.Name,
                    RoleName = roleName,
                    Status = pr.Result switch { "pass" => "completed", "fail" => "failed", "skip" => "skipped", _ => "completed" },
                    DurationSeconds = pr.DurationSeconds > 0 ? pr.DurationSeconds : null,
                    WorkerOutput = workerOutput,
                    TotalTests = isTestPhase ? (summary.TestCounts?.Total ?? 0) : 0,
                    PassedTests = isTestPhase ? (summary.TestCounts?.Passed ?? 0) : 0,
                    FailedTests = isTestPhase ? (summary.TestCounts?.Failed ?? 0) : 0,
                    ReviewVerdict = isReviewPhase ? summary.ReviewVerdict : null,
                    BrainPrompt = summaryPhasePrompts.BrainPrompt,
                    WorkerPrompt = summaryPhasePrompts.WorkerPrompt,
                    Clarifications = summaryClarifications ?? [],
                });
            }

            iterations.Add(new IterationViewInfo
            {
                Number = summary.Iteration,
                Phases = phases,
                IsCurrent = false,
                PlanningBrainPrompt = summaryPlanningPrompt,
                PlanningBrainResponse = summaryPlanningResponse,
            });
        }

        // Build view for the current/unsummarized iteration from pipeline state
        if (pipeline is not null && !iterations.Any(i => i.Number == pipeline.Iteration))
        {
            var currentIter = pipeline.Iteration;
            var isCurrent = pipeline.Phase is not GoalPhase.Done and not GoalPhase.Failed;

            // Determine the planning phase status
            var planningStatus = pipeline.Phase == GoalPhase.Planning ? "active" : "completed";

            var craftPrompts = ExtractCraftPrompts(pipeline.Conversation, currentIter);
            var (planningBrainPrompt, planningBrainResponse) = ExtractPlanningPrompts(pipeline.Conversation, currentIter);

            // Collect all clarifications from this pipeline, grouped by phase name
            var clarificationsByPhase = pipeline.Clarifications
                .Where(c => c.Iteration == currentIter)
                .GroupBy(c => c.Phase, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var currentPhases = new List<PhaseViewInfo>
            {
                new PhaseViewInfo
                {
                    Name = "Planning",
                    RoleName = "",
                    Status = planningStatus,
                    ProgressReports = planningStatus == "active" && pipeline.PhaseStartedAt.HasValue
                        ? progress.Where(p => p.Timestamp >= pipeline.PhaseStartedAt.Value).ToList()
                        : [],
                },
            };

            // CRITICAL: Only show Planning alone when actively in Planning phase.
            // Once past Planning, ALWAYS show worker phases (even if Plan is null).
            if (pipeline.Phase != GoalPhase.Planning)
            {
                var planPhases = pipeline.Plan?.Phases ?? [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging];
                var failedFound = false;

                foreach (var phase in planPhases)
                {
                    string status;
                    if (pipeline.StateMachine.CompletedPhases.Contains(phase))
                        status = "completed";
                    else if (isCurrent && phase == pipeline.Phase)
                        status = pipeline.IsWaitingForClarification ? "waiting" : "active";
                    else if (pipeline.Phase == GoalPhase.Failed && !failedFound)
                        { status = "failed"; failedFound = true; }
                    else if (pipeline.Phase == GoalPhase.Done)
                        status = "skipped";
                    else
                        status = "pending";

                    var roleName = GetRoleNameSafe(phase);
                    string? workerOutput = null;
                    if (!string.IsNullOrEmpty(roleName))
                        pipeline.PhaseOutputs.TryGetValue($"{roleName}-{currentIter}", out workerOutput);

                    var isTestPhase = phase == GoalPhase.Testing;
                    var isReviewPhase = phase == GoalPhase.Review;
                    var metrics = pipeline.Metrics;
                    var hasMetrics = status is "completed" or "active" or "failed" or "waiting";

                    clarificationsByPhase.TryGetValue(phase.ToString(), out var phaseClarifications);
                    craftPrompts.TryGetValue(roleName, out var phasePrompts);
                    currentPhases.Add(new PhaseViewInfo
                    {
                        Name = phase.ToDisplayName(),
                        RoleName = roleName,
                        Status = status,
                        WorkerOutput = workerOutput,
                        DurationSeconds = metrics.PhaseDurations.TryGetValue(phase.ToString(), out var dur) ? dur.TotalSeconds : null,
                        TotalTests = hasMetrics && isTestPhase ? metrics.TotalTests : 0,
                        PassedTests = hasMetrics && isTestPhase ? metrics.PassedTests : 0,
                        FailedTests = hasMetrics && isTestPhase ? metrics.FailedTests : 0,
                        CoveragePercent = hasMetrics && isTestPhase ? metrics.CoveragePercent : 0,
                        BuildSuccess = hasMetrics && isTestPhase && metrics.BuildSuccess,
                        ReviewVerdict = hasMetrics && isReviewPhase ? metrics.ReviewVerdict?.ToString() : null,
                        ReviewIssuesFound = hasMetrics && isReviewPhase ? metrics.ReviewIssuesFound : 0,
                        Issues = hasMetrics && isReviewPhase ? metrics.ReviewIssues.ToList() :
                                 hasMetrics && isTestPhase ? metrics.Issues.ToList() : [],
                        Verdict = hasMetrics && isTestPhase ? metrics.Verdict?.ToString() : null,
                        ProgressReports = (status == "active" || status == "waiting") && pipeline.PhaseStartedAt.HasValue
                            ? progress.Where(p => p.Timestamp >= pipeline.PhaseStartedAt.Value).ToList()
                            : [],
                        BrainPrompt = phasePrompts.BrainPrompt,
                        WorkerPrompt = phasePrompts.WorkerPrompt,
                        Clarifications = phaseClarifications ?? [],
                    });
                }
            }

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

        // Derive effective status from pipeline phase
        var effectiveStatus = pipeline?.Phase switch
        {
            GoalPhase.Done => GoalStatus.Completed,
            GoalPhase.Failed => GoalStatus.Failed,
            not null => GoalStatus.InProgress,
            _ => goal.Status,
        };

        return new GoalDetailInfo
        {
            GoalId = goalId,
            Description = goal.Description,
            Status = effectiveStatus,
            Priority = goal.Priority,
            Scope = goal.Scope,
            CurrentIteration = pipeline?.Iteration ?? 0,
            CurrentPhase = pipeline?.Phase.ToDisplayName() ?? "",
            CreatedAt = pipeline?.CreatedAt ?? goal.CreatedAt,
            CompletedAt = pipeline?.CompletedAt ?? goal.CompletedAt,
            ActiveTaskId = pipeline?.ActiveTaskId,
            CoderBranch = pipeline?.CoderBranch,
            Notes = goal.Notes,
            DependsOn = goal.DependsOn,
            Iterations = iterations,
            Conversation = pipeline?.Conversation.ToList() ?? [],
            MergeCommitHash = pipeline?.MergeCommitHash ?? goal.MergeCommitHash,
            RepositoryUrl = ResolveRepositoryUrl(goal),
            RepositoryNames = goal.RepositoryNames,
        };
    }

    private string? ResolveRepositoryUrl(Goal goal) => GetRepositoryUrl(goal);

    /// <summary>
    /// Extracts the planning Brain prompt and response for a given iteration from the conversation log.
    /// </summary>
    /// <param name="conversation">The pipeline conversation entries to search.</param>
    /// <param name="iteration">The iteration number to filter by.</param>
    /// <returns>A tuple of (userPrompt, assistantResponse), each of which may be null if not found.</returns>
    internal static (string? UserPrompt, string? AssistantResponse) ExtractPlanningPrompts(
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
    internal static Dictionary<string, (string? BrainPrompt, string? WorkerPrompt)> ExtractCraftPrompts(
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

    /// <summary>
    /// Resolves the URL of the first repository associated with a goal,
    /// stripping any <c>.git</c> suffix. Returns <c>null</c> if the goal has no
    /// repository names or the repository is not found in the configuration.
    /// </summary>
    /// <param name="goal">The goal whose primary repository URL is needed.</param>
    /// <returns>The repository base URL, or <c>null</c> if unavailable.</returns>
    public string? GetRepositoryUrl(Goal goal)
    {
        if (_config is null || goal.RepositoryNames.Count == 0)
            return null;

        var firstName = goal.RepositoryNames[0];
        var repoConfig = _config.Repositories.FirstOrDefault(r =>
            string.Equals(r.Name, firstName, StringComparison.OrdinalIgnoreCase));

        if (repoConfig is null)
            return null;

        var url = repoConfig.Url;
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];

        return url;
    }

    private static string PhaseNameToRoleName(string phaseName) => phaseName switch
    {
        "Coding" => "coder",
        "Testing" => "tester",
        "Review" => "reviewer",
        "Doc Writing" => "docwriter",
        "Improvement" => "improver",
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

    /// <summary>Returns all releases, optionally filtered by repository name.</summary>
    /// <param name="repository">Optional repository name to filter by, or <c>null</c> for all releases.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All matching releases ordered by creation date descending.</returns>
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
    /// <param name="releaseId">Release identifier to look up.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The release, or <c>null</c>.</returns>
    public async Task<Release?> GetReleaseDetailAsync(string releaseId, CancellationToken ct = default)
    {
        if (_goalStore is null)
            return null;
        return await _goalStore.GetReleaseAsync(releaseId, ct);
    }

    /// <summary>Updates a release's mutable fields via the goal store.</summary>
    /// <param name="release">The release with updated fields.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UpdateReleaseAsync(Release release, CancellationToken ct = default)
    {
        if (_goalStore is not null)
            await _goalStore.UpdateReleaseAsync(release, ct);
    }

    /// <summary>Creates a new release for the given repository and version tag.</summary>
    /// <param name="repository">Repository name this release belongs to.</param>
    /// <param name="version">Version tag for the release (e.g. "v1.2.0").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created release.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the goal store is not configured.</exception>
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
    /// <param name="releaseId">Release identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Goals associated with the release.</returns>
    public async Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default)
    {
        if (_goalStore is null)
            return [];
        return await _goalStore.GetGoalsByReleaseAsync(releaseId, ct);
    }

    /// <summary>Returns the CopilotHive assembly version string.</summary>
    public string GetVersion() => _version;

    /// <summary>Returns orchestrator info including uptime, versions, and model configuration.</summary>
    public OrchestratorInfo GetOrchestratorInfo()
    {
        var uptime = DateTime.UtcNow - _startTime;
        var roles = new[] { "coder", "tester", "reviewer", "docwriter", "improver" };

        return new OrchestratorInfo
        {
            Uptime = uptime,
            Version = _version,
            SharpCoderVersion = _sharpCoderVersion,
            ServerTime = DateTime.UtcNow,
            RoleModels = roles.ToDictionary(r => r, r => _config?.GetModelForRole(r) ?? Constants.DefaultModel),
            BrainModel = _brain?.GetStats()?.Model ?? "(not configured)",
            ComposerModel = _composer?.GetStats()?.Model ?? "(not configured)",
        };
    }

    /// <inheritdoc />
    public void Dispose() => _timer.Dispose();
}

/// <summary>Snapshot of all dashboard state at a point in time.</summary>
public sealed class DashboardSnapshot
{
    /// <summary>All known goals.</summary>
    public List<Goal> Goals { get; init; } = [];
    /// <summary>Connected workers.</summary>
    public List<WorkerInfo> Workers { get; init; } = [];
    /// <summary>Active goal pipelines.</summary>
    public List<PipelineInfo> Pipelines { get; init; } = [];
    /// <summary>Count of draft goals.</summary>
    public int DraftGoals { get; init; }
    /// <summary>Count of pending goals.</summary>
    public int PendingGoals { get; init; }
    /// <summary>Count of in-progress goals.</summary>
    public int ActiveGoals { get; init; }
    /// <summary>Count of completed goals.</summary>
    public int CompletedGoals { get; init; }
    /// <summary>Count of failed goals.</summary>
    public int FailedGoals { get; init; }
    /// <summary>Total connected workers.</summary>
    public int TotalWorkers { get; init; }
    /// <summary>Workers currently executing tasks.</summary>
    public int BusyWorkers { get; init; }
    /// <summary>Workers waiting for work.</summary>
    public int IdleWorkers { get; init; }
}

/// <summary>Worker state for the dashboard.</summary>
public sealed class WorkerInfo
{
    /// <summary>Worker identifier.</summary>
    public string Id { get; init; } = "";
    /// <summary>Current role name.</summary>
    public string Role { get; init; } = "";
    /// <summary>Whether the worker is busy.</summary>
    public bool IsBusy { get; init; }
    /// <summary>Current task ID, if any.</summary>
    public string? CurrentTaskId { get; init; }
    /// <summary>Last heartbeat timestamp.</summary>
    public DateTime LastHeartbeat { get; init; }
    /// <summary>Connection timestamp.</summary>
    public DateTime ConnectedAt { get; init; }
    /// <summary>Model used for the current task, or <c>null</c> when idle.</summary>
    public string? CurrentModel { get; init; }

    /// <summary>
    /// Returns the model string to display for this worker: the task-specific
    /// <see cref="CurrentModel"/> if set, otherwise the role-default from
    /// <paramref name="roleModels"/>, or <c>null</c> if neither is available.
    /// </summary>
    /// <param name="roleModels">Role-to-model mapping from <see cref="OrchestratorInfo.RoleModels"/>.</param>
    /// <returns>The display model string, or <c>null</c>.</returns>
    public string? GetDisplayModel(IDictionary<string, string> roleModels)
    {
        return CurrentModel
            ?? (Role != "Unspecified" && roleModels.TryGetValue(Role.ToLowerInvariant(), out var m) ? m : null);
    }
}

/// <summary>Pipeline state for the dashboard.</summary>
public sealed class PipelineInfo
{
    /// <summary>Goal identifier.</summary>
    public string GoalId { get; init; } = "";
    /// <summary>Goal description.</summary>
    public string Description { get; init; } = "";
    /// <summary>Current phase name.</summary>
    public string Phase { get; init; } = "";
    /// <summary>Ordered phase names from the iteration plan.</summary>
    public List<string> Phases { get; init; } = [];
    /// <summary>Current iteration number.</summary>
    public int Iteration { get; init; }
    /// <summary>Active task ID, if any.</summary>
    public string? ActiveTaskId { get; init; }
    /// <summary>Pipeline creation timestamp.</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>Total test count.</summary>
    public int TotalTests { get; init; }
    /// <summary>Passed test count.</summary>
    public int PassedTests { get; init; }
    /// <summary>Failed test count.</summary>
    public int FailedTests { get; init; }
    /// <summary>Code coverage percentage.</summary>
    public double CoveragePercent { get; init; }
}

/// <summary>Orchestrator system info for the dashboard.</summary>
public sealed class OrchestratorInfo
{
    /// <summary>Server uptime.</summary>
    public TimeSpan Uptime { get; init; }
    /// <summary>CopilotHive assembly version.</summary>
    public string Version { get; init; } = "";
    /// <summary>SharpCoder assembly version.</summary>
    public string? SharpCoderVersion { get; init; }
    /// <summary>Current UTC server time.</summary>
    public DateTime ServerTime { get; init; }
    /// <summary>Model configured for the Brain.</summary>
    public string BrainModel { get; init; } = "";
    /// <summary>Model configured for the Composer.</summary>
    public string ComposerModel { get; init; } = "";
    /// <summary>Model configured per worker role.</summary>
    public Dictionary<string, string> RoleModels { get; init; } = [];
}

/// <summary>Rich detail info for the goal detail page.</summary>
public sealed class GoalDetailInfo
{
    /// <summary>Goal identifier.</summary>
    public string GoalId { get; init; } = "";
    /// <summary>Goal description.</summary>
    public string Description { get; init; } = "";
    /// <summary>Effective goal status (derived from pipeline phase when active).</summary>
    public GoalStatus Status { get; init; }
    /// <summary>Goal priority level.</summary>
    public GoalPriority Priority { get; init; }
    /// <summary>Goal scope.</summary>
    public GoalScope Scope { get; init; }
    /// <summary>Current iteration number (zero if not started).</summary>
    public int CurrentIteration { get; init; }
    /// <summary>Name of the current pipeline phase.</summary>
    public string CurrentPhase { get; init; } = "";
    /// <summary>When the goal was created.</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>When the goal completed, if finished.</summary>
    public DateTime? CompletedAt { get; init; }
    /// <summary>Currently active task ID, if any.</summary>
    public string? ActiveTaskId { get; init; }
    /// <summary>Feature branch used by the coder.</summary>
    public string? CoderBranch { get; init; }
    /// <summary>Informational notes attached to the goal.</summary>
    public List<string> Notes { get; init; } = [];
    /// <summary>IDs of goals that must complete before this goal can be dispatched.</summary>
    public List<string> DependsOn { get; init; } = [];
    /// <summary>Per-iteration detail with phases.</summary>
    public List<IterationViewInfo> Iterations { get; init; } = [];
    /// <summary>Brain conversation log.</summary>
    public List<ConversationEntry> Conversation { get; init; } = [];
    /// <summary>SHA-1 hash of the merge commit that landed this goal's changes, or <c>null</c> if not yet merged.</summary>
    public string? MergeCommitHash { get; init; }
    /// <summary>URL of the primary repository for this goal (with .git suffix removed), or <c>null</c> if not resolved.</summary>
    public string? RepositoryUrl { get; init; }
    /// <summary>Repository names associated with this goal.</summary>
    public List<string> RepositoryNames { get; init; } = [];
}

/// <summary>Detail for a single iteration in the goal timeline.</summary>
public sealed class IterationViewInfo
{
    /// <summary>One-based iteration number.</summary>
    public int Number { get; init; }
    /// <summary>Phases executed in this iteration.</summary>
    public List<PhaseViewInfo> Phases { get; init; } = [];
    /// <summary>Whether this is the currently executing iteration.</summary>
    public bool IsCurrent { get; init; }
    /// <summary>Brain's reasoning for the iteration plan, or null if not yet planned.</summary>
    public string? PlanReason { get; init; }
    /// <summary>Brain prompt (user message) sent during the planning phase, or null if not available.</summary>
    public string? PlanningBrainPrompt { get; init; }
    /// <summary>Brain response (assistant message) from the planning phase, or null if not available.</summary>
    public string? PlanningBrainResponse { get; init; }
}

/// <summary>Detail for a single phase within an iteration.</summary>
public sealed class PhaseViewInfo
{
    /// <summary>Display name of the phase (e.g. "Coding", "Testing").</summary>
    public string Name { get; init; } = "";
    /// <summary>Worker role name (e.g. "coder", "tester"), empty for non-worker phases.</summary>
    public string RoleName { get; init; } = "";
    /// <summary>Phase status: "completed", "failed", "active", "pending", or "skipped".</summary>
    public string Status { get; init; } = "";
    /// <summary>Overall verdict for this phase.</summary>
    public string? Verdict { get; init; }
    /// <summary>Wall-clock duration in seconds, if available.</summary>
    public double? DurationSeconds { get; init; }
    /// <summary>Worker output for the phase, preferring persisted PhaseResult.WorkerOutput when available, falling back to pipeline PhaseOutputs.</summary>
    public string? WorkerOutput { get; init; }
    /// <summary>Total tests discovered (Testing phase only).</summary>
    public int TotalTests { get; init; }
    /// <summary>Tests that passed (Testing phase only).</summary>
    public int PassedTests { get; init; }
    /// <summary>Tests that failed (Testing phase only).</summary>
    public int FailedTests { get; init; }
    /// <summary>Code coverage percentage (Testing phase only).</summary>
    public double CoveragePercent { get; init; }
    /// <summary>Whether the build succeeded (Testing phase only).</summary>
    public bool BuildSuccess { get; init; }
    /// <summary>Review verdict string (Review phase only).</summary>
    public string? ReviewVerdict { get; init; }
    /// <summary>Number of issues found during review (Review phase only).</summary>
    public int ReviewIssuesFound { get; init; }
    /// <summary>List of issues found.</summary>
    public List<string> Issues { get; init; } = [];
    /// <summary>Progress reports during this phase.</summary>
    public List<ProgressEntry> ProgressReports { get; init; } = [];
    /// <summary>Brain prompt (user message) sent when crafting the worker prompt for this phase, or null if not available.</summary>
    public string? BrainPrompt { get; init; }
    /// <summary>Crafted worker prompt (Brain assistant message) for this phase, or null if not available.</summary>
    public string? WorkerPrompt { get; init; }
    /// <summary>Clarification Q&amp;As that occurred during this phase (matched by iteration and phase name).</summary>
    public List<ClarificationEntry> Clarifications { get; init; } = [];
}
