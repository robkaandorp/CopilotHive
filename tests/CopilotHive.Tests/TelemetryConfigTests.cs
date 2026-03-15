using GitHub.Copilot.SDK;

namespace CopilotHive.Tests;

public class TelemetryConfigTests
{
    [Fact]
    public void CopilotClientOptions_WithTelemetryConfig_WorkerRole()
    {
        var role = "coder";
        var options = new CopilotClientOptionsWithTelemetry
        {
            Telemetry = new TelemetryConfig
            {
                FilePath = $"/app/state/otel-{role}.jsonl",
                ExporterType = "file",
                SourceName = $"copilothive-worker-{role}",
                CaptureContent = true
            }
        };
        Assert.Equal($"/app/state/otel-{role}.jsonl", options.Telemetry.FilePath);
        Assert.Equal("file", options.Telemetry.ExporterType);
        Assert.Equal($"copilothive-worker-{role}", options.Telemetry.SourceName);
        Assert.True(options.Telemetry.CaptureContent);
    }

    [Fact]
    public void CopilotClientOptions_WithTelemetryConfig_Orchestrator()
    {
        var options = new CopilotClientOptionsWithTelemetry
        {
            Telemetry = new TelemetryConfig
            {
                FilePath = "/app/state/otel-orchestrator.jsonl",
                ExporterType = "file",
                SourceName = "copilothive-orchestrator",
                CaptureContent = true
            }
        };
        Assert.Equal("/app/state/otel-orchestrator.jsonl", options.Telemetry.FilePath);
        Assert.Equal("file", options.Telemetry.ExporterType);
        Assert.Equal("copilothive-orchestrator", options.Telemetry.SourceName);
        Assert.True(options.Telemetry.CaptureContent);
    }

    [Fact]
    public void CopilotClientOptions_WithTelemetryConfig_Brain()
    {
        var options = new CopilotClientOptionsWithTelemetry
        {
            Telemetry = new TelemetryConfig
            {
                FilePath = "/app/state/otel-brain.jsonl",
                ExporterType = "file",
                SourceName = "copilothive-brain",
                CaptureContent = true
            }
        };
        Assert.Equal("/app/state/otel-brain.jsonl", options.Telemetry.FilePath);
        Assert.Equal("file", options.Telemetry.ExporterType);
        Assert.Equal("copilothive-brain", options.Telemetry.SourceName);
        Assert.True(options.Telemetry.CaptureContent);
    }
}
