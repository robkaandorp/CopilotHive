using System.Reflection;
using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Knowledge;
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
    private readonly KnowledgeGraph? _knowledgeGraph;
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
        IGoalStore? goalStore = null,
        KnowledgeGraph? knowledgeGraph = null)
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
        _knowledgeGraph = knowledgeGraph;
        _timer = new Timer(_ => PollAndNotify(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
    }

    // ── Timer callback ─────────────────────────────────────────────────────────

    /// <summary>
    /// Timer callback: refreshes cached pending goals asynchronously on the thread pool
    /// then fires <see cref="OnStateChanged"/>.
    /// </summary>
    private async void PollAndNotify()
    {
        try
        {
            var goals = new List<Goal>();
            foreach (var source in _goalManager.Sources)
            {
                var pending = await source.GetPendingGoalsAsync();
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

    // ── Snapshot ───────────────────────────────────────────────────────────────

    /// <summary>Creates a snapshot of current system state.</summary>
    public async Task<DashboardSnapshot> GetSnapshot()
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
            var allGoals = await _goalStore.GetAllGoalsAsync();
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
                ContextUsagePercent = w.ContextUsagePercent,
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
        };
    }

    // ── Goal detail ────────────────────────────────────────────────────────────

    /// <summary>Builds a rich detail view for a specific goal, including per-iteration phase info.</summary>
    public async Task<GoalDetailInfo?> GetGoalDetail(string goalId)
    {
        var snap = await GetSnapshot();
        var goal = snap.Goals.FirstOrDefault(g => g.Id == goalId);
        if (goal is null)
            return null;

        // GetSnapshot uses GetAllGoalsAsync which does NOT populate IterationSummaries.
        // For the detail view we need the full goal with summaries loaded from the store.
        Goal? fullGoalWithSummaries = null;
        if (goal.IterationSummaries.Count == 0 && _goalStore is not null)
        {
            fullGoalWithSummaries = await _goalStore.GetGoalAsync(goalId);
        }

        var pipeline = _pipelineManager.GetByGoalId(goalId);
        return GoalDetailViewBuilder.Build(goal, goalId, pipeline, fullGoalWithSummaries, _config);
    }

    // ── Log access ─────────────────────────────────────────────────────────────

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

    /// <summary>Returns recent worker progress reports from active pipelines.</summary>
    public IReadOnlyList<ProgressEntry> GetRecentProgress(int count = 50)
    {
        // Read from all active pipelines' per-pipeline logs
        var pipelines = _pipelineManager.GetAllPipelines();
        var all = pipelines
            .SelectMany(p => p.ProgressReports)
            .OrderBy(e => e.Timestamp)
            .Take(count)
            .ToList();

        // Fall back to global log for any entries not captured by pipelines
        // (e.g., progress reports from before this refactor)
        var globalEntries = _progressLog.GetRecent(count * 2)
            .Where(e => !all.Any(a => a.Timestamp == e.Timestamp && a.WorkerId == e.WorkerId && a.GoalId == e.GoalId));

        return all.Concat(globalEntries)
            .OrderByDescending(e => e.Timestamp)
            .Take(count)
            .ToList();
    }

    /// <summary>Returns recent progress reports for a specific worker from active pipelines.</summary>
    public IReadOnlyList<ProgressEntry> GetProgressForWorker(string workerId, int count = 100)
    {
        // Read from all active pipelines' per-pipeline logs
        var pipelines = _pipelineManager.GetAllPipelines();
        var pipelineEntries = pipelines
            .SelectMany(p => p.ProgressReports)
            .Where(e => e.WorkerId == workerId)
            .OrderBy(e => e.Timestamp)
            .Take(count)
            .ToList();

        // Supplement with global log for historical entries
        var globalEntries = _progressLog.GetRecent(500)
            .Where(e => e.WorkerId == workerId &&
                !pipelineEntries.Any(p => p.Timestamp == e.Timestamp && p.GoalId == e.GoalId))
            .OrderBy(e => e.Timestamp);

        return pipelineEntries.Concat(globalEntries)
            .OrderBy(e => e.Timestamp)
            .Take(count)
            .ToList();
    }

    /// <summary>Returns recent progress reports for a specific goal from the active pipeline.</summary>
    public IReadOnlyList<ProgressEntry> GetProgressForGoal(string goalId, int count = 100)
    {
        // Prefer the active pipeline's per-pipeline log (has Phase/Iteration data)
        var pipeline = _pipelineManager.GetByGoalId(goalId);
        if (pipeline is not null)
        {
            return pipeline.ProgressReports
                .OrderBy(e => e.Timestamp)
                .Take(count)
                .ToList();
        }

        // Fall back to global log for completed goals
        return _progressLog.GetRecent(500)
            .Where(e => e.GoalId == goalId)
            .OrderBy(e => e.Timestamp)
            .Take(count)
            .ToList();
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
            CompactionModel = _config?.Models?.CompactionModel,
        };
    }

    /// <inheritdoc />
    public void Dispose() => _timer.Dispose();
}
