using CopilotHive.Services;

namespace CopilotHive.Dashboard;

/// <summary>
/// Aggregates worker progress reports from active pipelines and the global
/// <see cref="ProgressLog"/> into views consumed by the dashboard.
/// </summary>
public sealed class ProgressReportService
{
    private readonly GoalPipelineManager _pipelineManager;
    private readonly ProgressLog _progressLog;

    /// <summary>Creates the service with required dependencies.</summary>
    public ProgressReportService(GoalPipelineManager pipelineManager, ProgressLog progressLog)
    {
        _pipelineManager = pipelineManager;
        _progressLog = progressLog;
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
}
