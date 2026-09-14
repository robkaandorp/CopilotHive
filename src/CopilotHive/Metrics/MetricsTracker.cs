using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Metrics;

/// <summary>
/// Loads and records per-iteration metrics, persisting each entry as a JSON file.
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
    private readonly ILogger<MetricsTracker>? _logger;

    /// <summary>
    /// Initialises a new <see cref="MetricsTracker"/> and loads any previously saved metrics from disk.
    /// </summary>
    /// <param name="metricsPath">Directory where iteration JSON files are stored.</param>
    /// <param name="logger">Optional logger; when omitted, log output is suppressed.</param>
    public MetricsTracker(string metricsPath, ILogger<MetricsTracker>? logger = null)
    {
        _metricsPath = Path.GetFullPath(metricsPath);
        _logger = logger;
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

        _logger?.LogInformation(
            "Recorded iteration {Iteration}: {PassedTests}/{TotalTests} tests passed, {CoveragePercent:F1}% coverage",
            metrics.Iteration, metrics.PassedTests, metrics.TotalTests, metrics.CoveragePercent);
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
