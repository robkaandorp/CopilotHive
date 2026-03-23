using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;

namespace CopilotHive.Dashboard;

/// <summary>
/// Aggregates live state from WorkerPool, GoalPipelineManager, and ApiGoalSource
/// into a snapshot that Blazor components can bind to. Polls every few seconds
/// and fires <see cref="OnStateChanged"/> to trigger UI re-renders.
/// </summary>
public sealed class DashboardStateService : IDisposable
{
    private readonly WorkerPool _workerPool;
    private readonly GoalPipelineManager _pipelineManager;
    private readonly GoalManager _goalManager;
    private readonly DashboardLogSink _logSink;
    private readonly ProgressLog _progressLog;
    private readonly IDistributedBrain? _brain;
    private readonly HiveConfigFile? _config;
    private readonly Timer _timer;
    private readonly DateTime _startTime = DateTime.UtcNow;
    private readonly string _version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
    private readonly string? _sharpCoderVersion = typeof(SharpCoder.CodingAgent).Assembly.GetName().Version?.ToString();

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
        HiveConfigFile? config = null)
    {
        _workerPool = workerPool;
        _pipelineManager = pipelineManager;
        _goalManager = goalManager;
        _logSink = logSink;
        _progressLog = progressLog;
        _brain = brain;
        _config = config;
        _timer = new Timer(_ => NotifyStateChanged(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
    }

    /// <summary>Creates a snapshot of current system state.</summary>
    public DashboardSnapshot GetSnapshot()
    {
        var workers = _workerPool.GetAllWorkers();
        var pipelines = _pipelineManager.GetAllPipelines();

        // Collect goals from all sources (API + File) and active pipelines.
        var goalsById = new Dictionary<string, Goal>();

        // Add pending goals from all registered sources
        foreach (var source in _goalManager.Sources)
        {
            var pending = source.GetPendingGoalsAsync().GetAwaiter().GetResult();
            foreach (var g in pending)
                goalsById.TryAdd(g.Id, g);
        }

        // Add API source goals (includes all statuses, not just pending)
        var apiSource = _goalManager.Sources.OfType<ApiGoalSource>().FirstOrDefault();
        if (apiSource is not null)
        {
            foreach (var g in apiSource.GetAllGoals())
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

    /// <summary>Returns recent worker progress reports.</summary>
    public IReadOnlyList<ProgressEntry> GetRecentProgress(int count = 50) => _progressLog.GetRecent(count);

    /// <summary>Returns recent progress reports for a specific worker.</summary>
    public IReadOnlyList<ProgressEntry> GetProgressForWorker(string workerId, int count = 100) =>
        _progressLog.GetRecent(500)
            .Where(e => e.WorkerId == workerId)
            .TakeLast(count)
            .ToList();

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
        };
    }

    private void NotifyStateChanged() => OnStateChanged?.Invoke();

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
    /// <summary>Model configured per worker role.</summary>
    public Dictionary<string, string> RoleModels { get; init; } = [];
}
