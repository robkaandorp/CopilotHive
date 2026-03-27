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
    private readonly string _version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
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
            var phases = new List<PhaseViewInfo>
            {
                new PhaseViewInfo
                {
                    Name = "Planning",
                    RoleName = "",
                    Status = "completed",
                },
            };

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
                });
            }

            iterations.Add(new IterationViewInfo
            {
                Number = summary.Iteration,
                Phases = phases,
                IsCurrent = false,
            });
        }

        // Build view for the current/unsummarized iteration from pipeline state
        if (pipeline is not null && !iterations.Any(i => i.Number == pipeline.Iteration))
        {
            var currentIter = pipeline.Iteration;
            var isCurrent = pipeline.Phase is not GoalPhase.Done and not GoalPhase.Failed;

            // Determine the planning phase status
            var planningStatus = pipeline.Phase == GoalPhase.Planning ? "active" : "completed";

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

            // Only show worker phases when we actually have a plan (not during Planning phase)
            if (pipeline.Phase != GoalPhase.Planning && pipeline.Plan is not null)
            {
                var planPhases = pipeline.Plan.Phases;
                var failedFound = false;

                foreach (var phase in planPhases)
                {
                    string status;
                    if (pipeline.StateMachine.CompletedPhases.Contains(phase))
                        status = "completed";
                    else if (isCurrent && phase == pipeline.Phase)
                        status = "active";
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
                    var hasMetrics = status is "completed" or "active" or "failed";

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
                        ProgressReports = status == "active" && pipeline.PhaseStartedAt.HasValue
                            ? progress.Where(p => p.Timestamp >= pipeline.PhaseStartedAt.Value).ToList()
                            : [],
                    });
                }
            }

            iterations.Add(new IterationViewInfo
            {
                Number = currentIter,
                Phases = currentPhases,
                IsCurrent = isCurrent,
                PlanReason = pipeline.Plan?.Reason,
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
        };
    }

    private string? ResolveRepositoryUrl(Goal goal) => GetRepositoryUrl(goal);

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
}
