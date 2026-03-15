using System.Text.Json;

namespace CopilotHive.Metrics;

/// <summary>
/// Loads, records, and compares per-iteration metrics, persisting each entry as a JSON file.
/// </summary>
public sealed class MetricsTracker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _metricsPath;
    private readonly List<IterationMetrics> _history = [];

    /// <summary>
    /// Initialises a new <see cref="MetricsTracker"/> and loads any previously saved metrics from disk.
    /// </summary>
    /// <param name="metricsPath">Directory where iteration JSON files are stored.</param>
    public MetricsTracker(string metricsPath)
    {
        _metricsPath = Path.GetFullPath(metricsPath);
        Directory.CreateDirectory(_metricsPath);
        LoadHistory();
    }

    /// <summary>All recorded iteration metrics in chronological order.</summary>
    public IReadOnlyList<IterationMetrics> History => _history;
    /// <summary>The most recently recorded metrics, or <c>null</c> if no iterations have been recorded yet.</summary>
    public IterationMetrics? Latest => _history.Count > 0 ? _history[^1] : null;

    /// <summary>
    /// Appends the given metrics to the in-memory history and writes a JSON file to disk.
    /// </summary>
    /// <param name="metrics">Metrics to record for the current iteration.</param>
    public void RecordIteration(IterationMetrics metrics)
    {
        _history.Add(metrics);

        var fileName = $"iteration-{metrics.Iteration:D3}.json";
        var filePath = Path.Combine(_metricsPath, fileName);
        var json = JsonSerializer.Serialize(metrics, JsonOptions);
        File.WriteAllText(filePath, json);

        Console.WriteLine($"[Metrics] Recorded iteration {metrics.Iteration}: " +
            $"{metrics.PassedTests}/{metrics.TotalTests} tests passed, " +
            $"{metrics.CoveragePercent:F1}% coverage");
    }

    /// <summary>
    /// Compares the given metrics with the previous iteration, if one exists.
    /// </summary>
    /// <param name="current">The current iteration's metrics.</param>
    /// <returns>A <see cref="MetricsComparison"/>, or <c>null</c> when fewer than two iterations have been recorded.</returns>
    public MetricsComparison? CompareWithPrevious(IterationMetrics current)
    {
        if (_history.Count < 2)
            return null;

        var previous = _history[^2];
        return new MetricsComparison
        {
            Previous = previous,
            Current = current,
            CoverageDelta = current.CoveragePercent - previous.CoveragePercent,
            TestCountDelta = current.TotalTests - previous.TotalTests,
            PassRateDelta = current.PassRate - previous.PassRate,
        };
    }

    /// <summary>
    /// Returns <c>true</c> when coverage or pass-rate has regressed relative to the previous iteration.
    /// </summary>
    /// <param name="current">The current iteration's metrics to evaluate.</param>
    /// <returns><c>true</c> if regression is detected; otherwise <c>false</c>.</returns>
    public bool HasRegressed(IterationMetrics current)
    {
        var comparison = CompareWithPrevious(current);
        if (comparison is null)
            return false;

        // If current extraction produced no test data, skip the test regression check entirely
        if (current.TotalTests == 0)
        {
            Console.WriteLine("Test metrics not extracted (TotalTests=0); skipping test regression check");
            return comparison.CoverageDelta < -1.0;
        }

        // Regression: coverage dropped OR pass rate dropped
        return comparison.CoverageDelta < -1.0 || comparison.PassRateDelta < -0.05;
    }

    private void LoadHistory()
    {
        if (!Directory.Exists(_metricsPath))
            return;

        var files = Directory.GetFiles(_metricsPath, "iteration-*.json")
            .OrderBy(f => f);

        foreach (var file in files)
        {
            var json = File.ReadAllText(file);
            var metrics = JsonSerializer.Deserialize<IterationMetrics>(json, JsonOptions);
            if (metrics is not null)
                _history.Add(metrics);
        }
    }
}

/// <summary>
/// Holds the result of comparing two consecutive iteration metrics.
/// </summary>
public sealed class MetricsComparison
{
    /// <summary>Metrics from the previous iteration.</summary>
    public required IterationMetrics Previous { get; init; }
    /// <summary>Metrics from the current iteration.</summary>
    public required IterationMetrics Current { get; init; }
    /// <summary>Change in coverage percentage (positive = improved).</summary>
    public double CoverageDelta { get; init; }
    /// <summary>Change in the total test count (positive = more tests).</summary>
    public int TestCountDelta { get; init; }
    /// <summary>Change in pass rate (positive = improved).</summary>
    public double PassRateDelta { get; init; }

    /// <summary>Returns a human-readable summary of coverage, test count, and pass-rate deltas.</summary>
    public override string ToString() =>
        $"Coverage: {CoverageDelta:+0.0;-0.0}%, Tests: {TestCountDelta:+0;-0}, PassRate: {PassRateDelta:+0.00;-0.00}";
}
