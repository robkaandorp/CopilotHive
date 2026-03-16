using System.Text.Json;
using CopilotHive.Services;

namespace CopilotHive.Tests;

/// <summary>
/// Unit tests for <see cref="TelemetryAggregator"/>.
/// </summary>
public sealed class TelemetryAggregatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TelemetryAggregator _aggregator = new();

    public TelemetryAggregatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── AggregateTelemetry ──────────────────────────────────────────────

    [Fact]
    public void AggregateTelemetry_ParsesAndAggregatesCorrectly()
    {
        WriteTraceLines("coder",
            MakeLine(inputTokens: 1_000, outputTokens: 500, durationMs: 20_000, cost: 0.05m),
            MakeLine(inputTokens: 2_000, outputTokens: 800, durationMs: 25_000, cost: 0.07m));

        var summary = _aggregator.AggregateTelemetry(_tempDir, ["coder"]);

        Assert.Single(summary.Roles);
        var coder = summary.Roles[0];
        Assert.Equal("coder", coder.Role);
        Assert.Equal(3_000, coder.InputTokens);
        Assert.Equal(1_300, coder.OutputTokens);
        Assert.Equal(45_000, coder.DurationMs);
        Assert.Equal(0.12m, coder.Cost);
        Assert.Equal(2, coder.ApiCalls);
    }

    [Fact]
    public void AggregateTelemetry_AggregatesMultipleRoles()
    {
        WriteTraceLines("coder",
            MakeLine(inputTokens: 12_500, outputTokens: 8_200, durationMs: 45_000, cost: 0.12m),
            MakeLine(inputTokens: 12_500, outputTokens: 8_200, durationMs: 45_000, cost: 0.12m));
        WriteTraceLines("reviewer",
            MakeLine(inputTokens: 8_000, outputTokens: 3_100, durationMs: 15_000, cost: 0.05m));
        WriteTraceLines("tester",
            MakeLine(inputTokens: 9_800, outputTokens: 4_500, durationMs: 20_000, cost: 0.07m));

        var summary = _aggregator.AggregateTelemetry(_tempDir, ["coder", "reviewer", "tester"]);

        Assert.Equal(3, summary.Roles.Count);

        var coder = summary.Roles.Single(r => r.Role == "coder");
        Assert.Equal(25_000, coder.InputTokens);
        Assert.Equal(2, coder.ApiCalls);

        var reviewer = summary.Roles.Single(r => r.Role == "reviewer");
        Assert.Equal(8_000, reviewer.InputTokens);
        Assert.Equal(1, reviewer.ApiCalls);
    }

    [Fact]
    public void AggregateTelemetry_MissingFile_OmitsRole()
    {
        // Only write coder; tester file does not exist
        WriteTraceLines("coder", MakeLine(inputTokens: 100, outputTokens: 50, durationMs: 1_000, cost: 0.01m));

        var summary = _aggregator.AggregateTelemetry(_tempDir, ["coder", "tester"]);

        Assert.Single(summary.Roles);
        Assert.Equal("coder", summary.Roles[0].Role);
    }

    [Fact]
    public void AggregateTelemetry_NonExistentStateDir_ReturnsEmptyRoles()
    {
        var missingDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        var summary = _aggregator.AggregateTelemetry(missingDir, ["coder", "reviewer", "tester"]);

        Assert.Empty(summary.Roles);
    }

    [Fact]
    public void AggregateTelemetry_EmptyFile_OmitsRole()
    {
        File.WriteAllText(Path.Combine(_tempDir, "traces-coder.jsonl"), string.Empty);

        var summary = _aggregator.AggregateTelemetry(_tempDir, ["coder"]);

        Assert.Empty(summary.Roles);
    }

    [Fact]
    public void AggregateTelemetry_BlankLinesOnly_OmitsRole()
    {
        File.WriteAllText(Path.Combine(_tempDir, "traces-coder.jsonl"), "\n\n   \n");

        var summary = _aggregator.AggregateTelemetry(_tempDir, ["coder"]);

        Assert.Empty(summary.Roles);
    }

    [Fact]
    public void AggregateTelemetry_MalformedLine_SkipsIt()
    {
        var path = Path.Combine(_tempDir, "traces-coder.jsonl");
        File.WriteAllLines(path, [
            "NOT_VALID_JSON",
            MakeLine(inputTokens: 500, outputTokens: 200, durationMs: 5_000, cost: 0.02m),
        ]);

        var summary = _aggregator.AggregateTelemetry(_tempDir, ["coder"]);

        Assert.Single(summary.Roles);
        Assert.Equal(1, summary.Roles[0].ApiCalls); // only the valid line
        Assert.Equal(500, summary.Roles[0].InputTokens);
    }

    // ── FormatSummary ───────────────────────────────────────────────────

    [Fact]
    public void FormatSummary_ReturnsExpectedString()
    {
        var summary = new TelemetrySummary([
            new TelemetryRoleData("coder",    12_500, 8_200, 45_000, 0.12m, 2),
            new TelemetryRoleData("reviewer",  8_000, 3_100, 15_000, 0.05m, 1),
            new TelemetryRoleData("tester",    9_800, 4_500, 20_000, 0.07m, 1),
        ]);

        var formatted = _aggregator.FormatSummary(summary);

        Assert.Contains("Token usage this iteration:", formatted);
        Assert.Contains(" - coder: 12,500 input + 8,200 output tokens, 2 API calls, 45s, $0.12", formatted);
        Assert.Contains(" - reviewer: 8,000 input + 3,100 output tokens, 1 API call, 15s, $0.05", formatted);
        Assert.Contains(" - tester: 9,800 input + 4,500 output tokens, 1 API call, 20s, $0.07", formatted);
        Assert.Contains(" Total: 46,100 tokens, $0.24", formatted);
    }

    [Fact]
    public void FormatSummary_SingularApiCall_WhenCountIsOne()
    {
        var summary = new TelemetrySummary([
            new TelemetryRoleData("coder", 1_000, 500, 10_000, 0.01m, 1),
        ]);

        var formatted = _aggregator.FormatSummary(summary);

        Assert.Contains("1 API call,", formatted);
        Assert.DoesNotContain("1 API calls", formatted);
    }

    [Fact]
    public void FormatSummary_PluralApiCalls_WhenCountIsGreaterThanOne()
    {
        var summary = new TelemetrySummary([
            new TelemetryRoleData("coder", 1_000, 500, 10_000, 0.01m, 3),
        ]);

        var formatted = _aggregator.FormatSummary(summary);

        Assert.Contains("3 API calls,", formatted);
    }

    [Fact]
    public void FormatSummary_DurationRoundedToWholeSeconds()
    {
        // 1500ms → 2s (rounded)
        var summary = new TelemetrySummary([
            new TelemetryRoleData("coder", 1_000, 500, 1_500, 0.01m, 1),
        ]);

        var formatted = _aggregator.FormatSummary(summary);

        Assert.Contains("2s,", formatted);
    }

    [Fact]
    public void FormatSummary_EmptySummary_ReturnsEmptyString()
    {
        var summary = new TelemetrySummary([]);

        var formatted = _aggregator.FormatSummary(summary);

        Assert.Equal(string.Empty, formatted);
    }

    [Fact]
    public void FormatSummary_CostFormattedToTwoDecimalPlaces()
    {
        var summary = new TelemetrySummary([
            new TelemetryRoleData("coder", 100, 50, 1_000, 0.1m, 1),
        ]);

        var formatted = _aggregator.FormatSummary(summary);

        Assert.Contains("$0.10", formatted);
    }

    // ── ClearTelemetryFiles ─────────────────────────────────────────────

    [Fact]
    public void ClearTelemetryFiles_EmptiesExistingFiles()
    {
        var path = Path.Combine(_tempDir, "traces-coder.jsonl");
        File.WriteAllText(path, MakeLine(inputTokens: 100, outputTokens: 50, durationMs: 1_000, cost: 0.01m));
        Assert.True(new FileInfo(path).Length > 0);

        _aggregator.ClearTelemetryFiles(_tempDir, ["coder"]);

        Assert.Equal(string.Empty, File.ReadAllText(path));
    }

    [Fact]
    public void ClearTelemetryFiles_MissingFile_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            _aggregator.ClearTelemetryFiles(_tempDir, ["nonexistent"]));

        Assert.Null(ex);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private void WriteTraceLines(string role, params string[] lines)
    {
        var path = Path.Combine(_tempDir, $"traces-{role}.jsonl");
        File.WriteAllLines(path, lines);
    }

    private static string MakeLine(long inputTokens, long outputTokens, double durationMs, decimal cost)
    {
        var entry = new
        {
            model = "gpt-4o",
            input_tokens = inputTokens,
            output_tokens = outputTokens,
            duration_ms = durationMs,
            cost,
            api_call_id = Guid.NewGuid().ToString(),
        };
        return JsonSerializer.Serialize(entry);
    }
}
