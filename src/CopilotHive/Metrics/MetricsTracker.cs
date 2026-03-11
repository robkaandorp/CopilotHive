using System.Text.Json;

namespace CopilotHive.Metrics;

public sealed class MetricsTracker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _metricsPath;
    private readonly List<IterationMetrics> _history = [];

    public MetricsTracker(string metricsPath)
    {
        _metricsPath = Path.GetFullPath(metricsPath);
        Directory.CreateDirectory(_metricsPath);
        LoadHistory();
    }

    public IReadOnlyList<IterationMetrics> History => _history;
    public IterationMetrics? Latest => _history.Count > 0 ? _history[^1] : null;

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

    public bool HasRegressed(IterationMetrics current)
    {
        var comparison = CompareWithPrevious(current);
        if (comparison is null)
            return false;

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

public sealed class MetricsComparison
{
    public required IterationMetrics Previous { get; init; }
    public required IterationMetrics Current { get; init; }
    public double CoverageDelta { get; init; }
    public int TestCountDelta { get; init; }
    public double PassRateDelta { get; init; }

    public override string ToString() =>
        $"Coverage: {CoverageDelta:+0.0;-0.0}%, Tests: {TestCountDelta:+0;-0}, PassRate: {PassRateDelta:+0.00;-0.00}";
}
