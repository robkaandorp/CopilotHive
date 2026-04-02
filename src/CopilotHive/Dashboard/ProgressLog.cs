using System.Collections.Concurrent;
using CopilotHive.Services;

namespace CopilotHive.Dashboard;

/// <summary>
/// Captures worker progress reports (<c>report_progress</c> tool calls)
/// in a circular buffer for the dashboard.
/// </summary>
public sealed class ProgressLog
{
    private readonly ConcurrentQueue<ProgressEntry> _entries = new();
    private readonly int _maxEntries;

    /// <summary>Creates a progress log with the given capacity.</summary>
    public ProgressLog(int maxEntries = 200) => _maxEntries = maxEntries;

    /// <summary>Records a progress report from a worker.</summary>
    public void Add(string workerId, string goalId, string status, string details)
    {
        _entries.Enqueue(new ProgressEntry
        {
            Timestamp = DateTime.UtcNow,
            WorkerId = workerId,
            GoalId = goalId,
            Status = status,
            Details = details,
        });

        while (_entries.Count > _maxEntries)
            _entries.TryDequeue(out _);
    }

    /// <summary>
    /// Records a clarification event as a distinct progress entry with status <c>"clarification"</c>.
    /// </summary>
    /// <param name="clarification">The clarification entry to record.</param>
    public void AddClarification(ClarificationEntry clarification)
    {
        _entries.Enqueue(new ProgressEntry
        {
            Timestamp = clarification.Timestamp,
            WorkerId = clarification.WorkerRole,
            GoalId = clarification.GoalId,
            Status = "clarification",
            Details = $"Q: {clarification.Question} | A: {clarification.Answer} (answered by: {clarification.AnsweredBy})",
        });

        while (_entries.Count > _maxEntries)
            _entries.TryDequeue(out _);
    }

    /// <summary>Returns the most recent progress entries.</summary>
    public IReadOnlyList<ProgressEntry> GetRecent(int count = 50) =>
        _entries.Reverse().Take(count).Reverse().ToList();
}

/// <summary>A single worker progress report.</summary>
public sealed class ProgressEntry
{
    /// <summary>When the report was received.</summary>
    public DateTime Timestamp { get; init; }
    /// <summary>Worker that reported.</summary>
    public string WorkerId { get; init; } = "";
    /// <summary>Goal being worked on.</summary>
    public string GoalId { get; init; } = "";
    /// <summary>Pipeline phase when the report was generated.</summary>
    public string Phase { get; init; } = "";
    /// <summary>Iteration number when the report was generated.</summary>
    public int Iteration { get; init; }
    /// <summary>Status string from the worker.</summary>
    public string Status { get; init; } = "";
    /// <summary>Details message.</summary>
    public string Details { get; init; } = "";
}
