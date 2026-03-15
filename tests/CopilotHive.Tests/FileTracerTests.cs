using System.Text.Json;
using CopilotHive.Telemetry;
using GitHub.Copilot.SDK;

namespace CopilotHive.Tests;

/// <summary>
/// Unit tests for <see cref="FileTracer"/>: verifies that usage data is serialised
/// correctly to a JSONL file without throwing on errors.
/// </summary>
public sealed class FileTracerTests : IDisposable
{
    private readonly string _traceFile;

    public FileTracerTests()
    {
        _traceFile = Path.Combine(Path.GetTempPath(), $"copilot-traces-test-{Guid.NewGuid()}.jsonl");
    }

    public void Dispose()
    {
        if (File.Exists(_traceFile))
            File.Delete(_traceFile);
    }

    [Fact]
    public void WriteUsage_WritesJsonLineToFile()
    {
        var data = new AssistantUsageData
        {
            Model = "gpt-4o",
            InputTokens = 100,
            OutputTokens = 50,
            Duration = 1234.5,
        };

        FileTracer.WriteUsage(data, _traceFile, "coder");

        Assert.True(File.Exists(_traceFile));
        var line = File.ReadAllLines(_traceFile).Single();
        var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        Assert.Equal("gpt-4o", root.GetProperty("model").GetString());
        Assert.Equal(100, root.GetProperty("input_tokens").GetDouble());
        Assert.Equal(50, root.GetProperty("output_tokens").GetDouble());
        Assert.Equal(1234.5, root.GetProperty("duration_ms").GetDouble());
        Assert.Equal("coder", root.GetProperty("role").GetString());
    }

    [Fact]
    public void WriteUsage_AppendsMultipleLines()
    {
        var data1 = new AssistantUsageData { Model = "gpt-4o", InputTokens = 10 };
        var data2 = new AssistantUsageData { Model = "claude-3-5-sonnet", InputTokens = 20 };

        FileTracer.WriteUsage(data1, _traceFile, "tester");
        FileTracer.WriteUsage(data2, _traceFile, "tester");

        var lines = File.ReadAllLines(_traceFile);
        Assert.Equal(2, lines.Length);

        var doc1 = JsonDocument.Parse(lines[0]).RootElement;
        var doc2 = JsonDocument.Parse(lines[1]).RootElement;
        Assert.Equal("gpt-4o", doc1.GetProperty("model").GetString());
        Assert.Equal("claude-3-5-sonnet", doc2.GetProperty("model").GetString());
    }

    [Fact]
    public void WriteUsage_NullRole_WritesNullRoleField()
    {
        var data = new AssistantUsageData { Model = "gpt-4o" };

        FileTracer.WriteUsage(data, _traceFile, role: null);

        var line = File.ReadAllLines(_traceFile).Single();
        var doc = JsonDocument.Parse(line).RootElement;
        Assert.Equal(JsonValueKind.Null, doc.GetProperty("role").ValueKind);
    }

    [Fact]
    public void WriteUsage_InvalidPath_DoesNotThrow()
    {
        var data = new AssistantUsageData { Model = "gpt-4o" };

        // A path with null bytes is invalid on all platforms
        var badPath = "/no-such-dir-\0/traces.jsonl";

        // Must not throw — tracing errors are swallowed
        var ex = Record.Exception(() => FileTracer.WriteUsage(data, badPath, "brain"));
        Assert.Null(ex);
    }
}
