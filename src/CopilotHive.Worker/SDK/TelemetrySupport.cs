// Forward-compatible stub until GitHub.Copilot.SDK natively provides TelemetryConfig.
// These types mirror the expected SDK API and will be replaced when the SDK adds them.
namespace GitHub.Copilot.SDK;

/// <summary>
/// Configuration for native SDK telemetry export.
/// Stub until GitHub.Copilot.SDK provides this type natively.
/// </summary>
public sealed class TelemetryConfig
{
    /// <summary>Path to the JSONL file where telemetry spans are written.</summary>
    public string? FilePath { get; set; }

    /// <summary>Exporter backend type (e.g. "file").</summary>
    public string? ExporterType { get; set; }

    /// <summary>OpenTelemetry source/service name for emitted spans.</summary>
    public string? SourceName { get; set; }

    /// <summary>When true, prompt and response content is included in telemetry spans.</summary>
    public bool CaptureContent { get; set; }
}

/// <summary>
/// Extends <see cref="CopilotClientOptions"/> with a <see cref="Telemetry"/> property
/// until the SDK exposes it natively on the base class.
/// </summary>
public sealed class CopilotClientOptionsWithTelemetry : CopilotClientOptions
{
    /// <summary>Optional native SDK telemetry configuration.</summary>
    public TelemetryConfig? Telemetry { get; set; }
}
