using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotHive.Orchestration;

/// <summary>
/// Actions the orchestrator LLM can request.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OrchestratorActionType>))]
public enum OrchestratorActionType
{
    /// <summary>Spawn a coder worker to implement the goal.</summary>
    SpawnCoder,
    /// <summary>Spawn a reviewer worker to review the coder's output.</summary>
    SpawnReviewer,
    /// <summary>Spawn a tester worker to run and write tests.</summary>
    SpawnTester,
    /// <summary>Spawn the improver worker to suggest AGENTS.md changes.</summary>
    SpawnImprover,
    /// <summary>Spawn the doc-writer worker to update documentation.</summary>
    SpawnDocWriter,
    /// <summary>Send the code back to the coder with change requests.</summary>
    RequestChanges,
    /// <summary>Retry the current phase from the beginning.</summary>
    Retry,
    /// <summary>Merge the feature branch into main.</summary>
    Merge,
    /// <summary>The goal is complete; stop processing.</summary>
    Done,
    /// <summary>Skip the current phase and move on.</summary>
    Skip,
}

/// <summary>
/// A decision returned by the orchestrator LLM.
/// </summary>
public sealed record OrchestratorDecision
{
    /// <summary>The recommended next action.</summary>
    [JsonPropertyName("action")]
    public OrchestratorActionType Action { get; init; } = OrchestratorActionType.Done;

    /// <summary>The prompt to send to a worker, when the action spawns one.</summary>
    [JsonPropertyName("prompt")]
    public string? Prompt { get; init; }

    /// <summary>The brain's reasoning for choosing this action.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>The overall verdict (e.g. "PASS", "FAIL") when interpreting worker output.</summary>
    [JsonPropertyName("verdict")]
    public string? Verdict { get; init; }

    /// <summary>The reviewer's verdict (e.g. "APPROVED", "CHANGES_REQUESTED").</summary>
    [JsonPropertyName("review_verdict")]
    public string? ReviewVerdict { get; init; }

    /// <summary>List of issues identified by the brain.</summary>
    [JsonPropertyName("issues")]
    public List<string>? Issues { get; init; }

    /// <summary>Model tier requested by the Brain ('standard' or 'premium'). Defaults to 'standard'.</summary>
    [JsonPropertyName("model_tier")]
    public string? ModelTier { get; init; }
}

/// <summary>
/// A single entry in the orchestrator conversation log.
/// </summary>
/// <param name="Role">The role of the message sender (e.g. "user", "assistant", "system").</param>
/// <param name="Content">The text content of the message.</param>
public sealed record ConversationEntry(string Role, string Content);

/// <summary>
/// Result of executing a worker action, fed back to the orchestrator LLM.
/// </summary>
public sealed record WorkerResult
{
    /// <summary>The role of the worker that produced this result.</summary>
    public required string Role { get; init; }
    /// <summary>Raw text output from the worker.</summary>
    public required string Output { get; init; }
    /// <summary>Whether the worker task completed successfully.</summary>
    public bool Success { get; init; } = true;
}

/// <summary>
/// JSON serialization options shared across the protocol.
/// </summary>
public static class ProtocolJson
{
    /// <summary>Shared JSON serializer options using snake_case naming.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

}
