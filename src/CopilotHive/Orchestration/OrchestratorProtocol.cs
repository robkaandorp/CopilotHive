using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotHive.Orchestration;

/// <summary>
/// Actions the orchestrator LLM can request.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OrchestratorActionType>))]
public enum OrchestratorActionType
{
    SpawnCoder,
    SpawnReviewer,
    SpawnTester,
    Merge,
    Done,
    Skip,
}

/// <summary>
/// A decision returned by the orchestrator LLM.
/// </summary>
public sealed record OrchestratorDecision
{
    [JsonPropertyName("action")]
    public OrchestratorActionType Action { get; init; } = OrchestratorActionType.Done;

    [JsonPropertyName("prompt")]
    public string? Prompt { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("verdict")]
    public string? Verdict { get; init; }

    [JsonPropertyName("test_metrics")]
    public ExtractedTestMetrics? TestMetrics { get; init; }

    [JsonPropertyName("review_verdict")]
    public string? ReviewVerdict { get; init; }

    [JsonPropertyName("issues")]
    public List<string>? Issues { get; init; }
}

/// <summary>
/// Structured test metrics extracted by the orchestrator LLM from worker output.
/// </summary>
public sealed record ExtractedTestMetrics
{
    [JsonPropertyName("build_success")]
    public bool? BuildSuccess { get; init; }

    [JsonPropertyName("total_tests")]
    public int? TotalTests { get; init; }

    [JsonPropertyName("passed_tests")]
    public int? PassedTests { get; init; }

    [JsonPropertyName("failed_tests")]
    public int? FailedTests { get; init; }

    [JsonPropertyName("coverage_percent")]
    public double? CoveragePercent { get; init; }
}

/// <summary>
/// A single entry in the orchestrator conversation log.
/// </summary>
public sealed record ConversationEntry(string Role, string Content);

/// <summary>
/// Result of executing a worker action, fed back to the orchestrator LLM.
/// </summary>
public sealed record WorkerResult
{
    public required string Role { get; init; }
    public required string Output { get; init; }
    public bool Success { get; init; } = true;
}

/// <summary>
/// JSON serialization options shared across the protocol.
/// </summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>
    /// Attempts to extract a JSON object from text that may contain markdown fences.
    /// </summary>
    public static T? ParseFromLlmResponse<T>(string response) where T : class
    {
        // Try direct parse first
        try
        {
            return JsonSerializer.Deserialize<T>(response.Trim(), Options);
        }
        catch { }

        // Try extracting from markdown code block
        var jsonStart = response.IndexOf('{');
        var jsonEnd = response.LastIndexOf('}');
        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            try
            {
                var json = response[jsonStart..(jsonEnd + 1)];
                return JsonSerializer.Deserialize<T>(json, Options);
            }
            catch { }
        }

        return null;
    }
}
