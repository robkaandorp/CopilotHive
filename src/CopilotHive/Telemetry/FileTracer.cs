using System.Text.Json;
using Microsoft.Extensions.AI;

namespace CopilotHive.Telemetry;

/// <summary>
/// Appends OpenTelemetry-style span records to a JSONL trace file.
/// Each line is a JSON object containing token counts, latency, and model metadata.
/// </summary>
internal static class FileTracer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>
    /// Appends a Brain usage record from a <see cref="ChatResponse"/> to the trace file.
    /// </summary>
    public static void WriteBrainUsage(ChatResponse response, string filePath)
    {
        try
        {
            var entry = new
            {
                timestamp = DateTimeOffset.UtcNow,
                role = "brain",
                model = response.ModelId,
                input_tokens = response.Usage?.InputTokenCount,
                output_tokens = response.Usage?.OutputTokenCount,
                total_tokens = response.Usage?.TotalTokenCount,
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
