using CopilotHive.Metrics;

namespace CopilotHive.Tests;

public class MetricsTrackerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly MetricsTracker _tracker;

    public MetricsTrackerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-metrics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _tracker = new MetricsTracker(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void RecordIteration_PersistsToFile()
    {
        var metrics = new IterationMetrics
        {
            Iteration = 1,
            TotalTests = 10,
            PassedTests = 8,
            FailedTests = 2,
            CoveragePercent = 75.0,
        };

        _tracker.RecordIteration(metrics);

        var file = Path.Combine(_tempDir, "iteration-001.json");
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Latest_ReturnsLastRecorded()
    {
        _tracker.RecordIteration(new IterationMetrics { Iteration = 1, TotalTests = 5 });
        _tracker.RecordIteration(new IterationMetrics { Iteration = 2, TotalTests = 10 });

        Assert.NotNull(_tracker.Latest);
        Assert.Equal(2, _tracker.Latest!.Iteration);
        Assert.Equal(10, _tracker.Latest.TotalTests);
    }

    [Fact]
    public void Latest_ReturnsNull_WhenEmpty()
    {
        Assert.Null(_tracker.Latest);
    }

    [Fact]
    public void IterationMetrics_PassRate_CalculatesCorrectly()
    {
        var m = new IterationMetrics { TotalTests = 20, PassedTests = 15 };
        Assert.Equal(0.75, m.PassRate);
    }

    [Fact]
    public void IterationMetrics_PassRate_ZeroTests()
    {
        var m = new IterationMetrics { TotalTests = 0, PassedTests = 0 };
        Assert.Equal(0.0, m.PassRate);
    }

    [Fact]
    public void LoadsHistory_FromExistingFiles()
    {
        // Record some data with the first tracker
        _tracker.RecordIteration(new IterationMetrics { Iteration = 1, TotalTests = 5 });
        _tracker.RecordIteration(new IterationMetrics { Iteration = 2, TotalTests = 10 });

        // Create a new tracker pointing at the same directory
        var tracker2 = new MetricsTracker(_tempDir);

        Assert.Equal(2, tracker2.History.Count);
        Assert.Equal(10, tracker2.Latest!.TotalTests);
    }
}
