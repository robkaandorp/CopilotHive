using System.Text.Json;
using CopilotHive.Telemetry;
using Microsoft.Extensions.AI;

namespace CopilotHive.Tests;

/// <summary>
/// Unit tests for <see cref="FileTracer"/>: verifies that Brain usage data is serialised
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

    private static ChatResponse CreateResponse(string? model, int? inputTokens = null, int? outputTokens = null, int? totalTokens = null)
    {
        var response = new ChatResponse([])
        {
            ModelId = model,
            Usage = inputTokens is not null || outputTokens is not null || totalTokens is not null
                ? new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens, TotalTokenCount = totalTokens }
                : null,
        };
        return response;
    }

    [Fact]
    public void WriteBrainUsage_WritesJsonLineToFile()
    {
        var response = CreateResponse("gpt-4o", inputTokens: 100, outputTokens: 50, totalTokens: 150);

        FileTracer.WriteBrainUsage(response, _traceFile);

        Assert.True(File.Exists(_traceFile));
        var line = File.ReadAllLines(_traceFile).Single();
        var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        Assert.Equal("gpt-4o", root.GetProperty("model").GetString());
        Assert.Equal(100, root.GetProperty("input_tokens").GetInt32());
        Assert.Equal(50, root.GetProperty("output_tokens").GetInt32());
        Assert.Equal(150, root.GetProperty("total_tokens").GetInt32());
        Assert.Equal("brain", root.GetProperty("role").GetString());
    }

    [Fact]
    public void WriteBrainUsage_AppendsMultipleLines()
    {
        var r1 = CreateResponse("gpt-4o", inputTokens: 10);
        var r2 = CreateResponse("claude-3-5-sonnet", inputTokens: 20);

        FileTracer.WriteBrainUsage(r1, _traceFile);
        FileTracer.WriteBrainUsage(r2, _traceFile);

        var lines = File.ReadAllLines(_traceFile);
        Assert.Equal(2, lines.Length);

        var doc1 = JsonDocument.Parse(lines[0]).RootElement;
        var doc2 = JsonDocument.Parse(lines[1]).RootElement;
        Assert.Equal("gpt-4o", doc1.GetProperty("model").GetString());
        Assert.Equal("claude-3-5-sonnet", doc2.GetProperty("model").GetString());
    }

    [Fact]
    public void WriteBrainUsage_NullUsage_WritesNullFields()
    {
        var response = CreateResponse("gpt-4o");

        FileTracer.WriteBrainUsage(response, _traceFile);

        var line = File.ReadAllLines(_traceFile).Single();
        var doc = JsonDocument.Parse(line).RootElement;
        Assert.Equal(JsonValueKind.Null, doc.GetProperty("input_tokens").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.GetProperty("output_tokens").ValueKind);
    }

    [Fact]
    public void WriteBrainUsage_InvalidPath_DoesNotThrow()
    {
        var response = CreateResponse("gpt-4o", inputTokens: 10);

        // A path with null bytes is invalid on all platforms
        var badPath = "/no-such-dir-\0/traces.jsonl";

        // Must not throw — tracing errors are swallowed
        var ex = Record.Exception(() => FileTracer.WriteBrainUsage(response, badPath));
        Assert.Null(ex);
    }
}
