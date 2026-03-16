using System.Text.Json;

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
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;

                        inputTokens += GetLong(root, "input_tokens");
                        outputTokens += GetLong(root, "output_tokens");
                        durationMs += GetLong(root, "duration_ms");
                        cost += GetDecimal(root, "cost");
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
                $" - {role.Role}: {role.InputTokens:N0} input + {role.OutputTokens:N0} output tokens, " +
                $"{role.ApiCalls} {callLabel}, {seconds}s, ${role.Cost:F2}");
        }

        sb.Append($" Total: {grandTotalTokens:N0} tokens, ${grandTotalCost:F2}");

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
            catch
            {
                // Best-effort: never disrupt the main workflow
            }
        }
    }

    private static long GetLong(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var el))
        {
            if (el.ValueKind == JsonValueKind.Number)
                return el.TryGetInt64(out var v) ? v : (long)el.GetDouble();
        }
        return 0L;
    }

    private static decimal GetDecimal(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var el))
        {
            if (el.ValueKind == JsonValueKind.Number)
                return el.TryGetDecimal(out var v) ? v : (decimal)el.GetDouble();
        }
        return 0m;
    }
}
