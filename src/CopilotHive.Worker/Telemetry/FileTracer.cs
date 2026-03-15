using System.Text.Json;
using GitHub.Copilot.SDK;

namespace CopilotHive.Worker.Telemetry;

/// <summary>
/// Appends OpenTelemetry-style span records to a JSONL trace file.
/// Each line is a JSON object containing token counts, latency, and model metadata
/// sourced from <see cref="AssistantUsageData"/> events fired by the Copilot SDK.
/// </summary>
internal static class FileTracer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>
    /// Appends a single trace record to <paramref name="filePath"/> in JSONL format.
    /// Errors are silently swallowed so tracing never disrupts the main workflow.
    /// </summary>
    /// <param name="data">Usage data from the SDK's <see cref="AssistantUsageEvent"/>.</param>
    /// <param name="filePath">Absolute path to the JSONL trace file.</param>
    /// <param name="role">Optional role label (e.g. "coder", "brain", "orchestrator").</param>
    public static void WriteUsage(AssistantUsageData data, string filePath, string? role = null)
    {
        try
        {
            var entry = new
            {
                timestamp = DateTimeOffset.UtcNow,
                role,
                model = data.Model,
                input_tokens = data.InputTokens,
                output_tokens = data.OutputTokens,
                cache_read_tokens = data.CacheReadTokens,
                cache_write_tokens = data.CacheWriteTokens,
                duration_ms = data.Duration,
                cost = data.Cost,
                api_call_id = data.ApiCallId,
            };

            var line = JsonSerializer.Serialize(entry, JsonOptions);

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.AppendAllText(filePath, line + Environment.NewLine);
        }
        catch
        {
            // Tracing must never fail the calling operation
        }
    }
}
