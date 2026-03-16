using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotHive.Services;

/// <summary>
/// Per-role telemetry data aggregated from a JSONL trace file.
/// </summary>
public record TelemetryRoleData(
    string Role,
    long InputTokens,
    long OutputTokens,
    long DurationMs,
    decimal Cost,
    int ApiCalls);

/// <summary>
/// Aggregated telemetry summary across all roles for one iteration.
/// </summary>
public record TelemetrySummary(IReadOnlyList<TelemetryRoleData> Roles);

/// <summary>
/// Reads and aggregates per-role JSONL telemetry files written by <c>FileTracer</c>,
/// formats a human-readable summary, and clears the files after consumption.
/// </summary>
public sealed class TelemetryAggregator
{
    private sealed record TelemetryRecord
    {
        [JsonPropertyName("input_tokens")]
        public long InputTokens { get; set; }

        [JsonPropertyName("output_tokens")]
        public long OutputTokens { get; set; }

        [JsonPropertyName("cache_read_tokens")]
        public long CacheReadTokens { get; set; }

        [JsonPropertyName("cache_write_tokens")]
        public long CacheWriteTokens { get; set; }

        [JsonPropertyName("duration_ms")]
        public long DurationMs { get; set; }

        [JsonPropertyName("cost")]
        public decimal Cost { get; set; }

        [JsonPropertyName("api_call_id")]
        public string? ApiCallId { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Reads trace files for each role in <paramref name="roles"/> from
    /// <c>{stateDir}/traces-{role}.jsonl</c>, parses each line as a JSON object,
    /// and returns aggregated token/cost/duration totals per role.
    /// Roles with no data (missing or empty file) are omitted from the result.
    /// </summary>
    public TelemetrySummary AggregateTelemetry(string stateDir, IEnumerable<string> roles)
    {
        var result = new List<TelemetryRoleData>();

        foreach (var role in roles)
        {
            var path = Path.Combine(stateDir, $"traces-{role}.jsonl");
            if (!File.Exists(path))
                continue;

            long inputTokens = 0;
            long outputTokens = 0;
            long durationMs = 0;
            decimal cost = 0m;
            int apiCalls = 0;

            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        var record = JsonSerializer.Deserialize<TelemetryRecord>(line, JsonOptions);
                        if (record == null)
                            continue;

                        inputTokens += record.InputTokens;
                        outputTokens += record.OutputTokens;
                        durationMs += record.DurationMs;
                        cost += record.Cost;
                        apiCalls++;
                    }
                    catch (JsonException)
                    {
                        // Skip malformed lines
                    }
                }
            }
            catch (IOException)
            {
                // Skip unreadable files
            }

            if (apiCalls > 0)
                result.Add(new TelemetryRoleData(role, inputTokens, outputTokens, durationMs, cost, apiCalls));
        }

        return new TelemetrySummary(result);
    }

    /// <summary>
    /// Formats a <see cref="TelemetrySummary"/> as a human-readable multi-line string.
    /// Returns an empty string when there are no roles with data.
    /// </summary>
    public string FormatSummary(TelemetrySummary summary)
    {
        if (summary.Roles.Count == 0)
            return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Token usage this iteration:");

        long grandTotalTokens = 0;
        decimal grandTotalCost = 0m;

        foreach (var role in summary.Roles)
        {
            var totalTokens = role.InputTokens + role.OutputTokens;
            grandTotalTokens += totalTokens;
            grandTotalCost += role.Cost;

            var seconds = (long)Math.Round(role.DurationMs / 1000.0);
            var callLabel = role.ApiCalls == 1 ? "API call" : "API calls";

            sb.AppendLine(
                string.Format(CultureInfo.InvariantCulture,
                    " - {0}: {1:N0} input + {2:N0} output tokens, {3} {4}, {5}s, ${6:F2}",
                    role.Role, role.InputTokens, role.OutputTokens, role.ApiCalls, callLabel, seconds, role.Cost));
        }

        sb.Append(string.Format(CultureInfo.InvariantCulture,
            " Total: {0:N0} tokens, ${1:F2}", grandTotalTokens, grandTotalCost));

        return sb.ToString();
    }

    /// <summary>
    /// Clears telemetry files for each role by overwriting with an empty string.
    /// Only acts on files that exist; errors are silently ignored.
    /// </summary>
    public void ClearTelemetryFiles(string stateDir, IEnumerable<string> roles)
    {
        foreach (var role in roles)
        {
            var path = Path.Combine(stateDir, $"traces-{role}.jsonl");
            if (!File.Exists(path))
                continue;

            try
            {
                File.WriteAllText(path, string.Empty);
            }
            catch (IOException)
            {
                // ignore — file may be in use or inaccessible
            }
            catch (UnauthorizedAccessException)
            {
                // ignore — no write permission
            }
        }
    }

}
