using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotHive.Orchestration;

/// <summary>
/// A single entry in the orchestrator conversation log.
/// </summary>
/// <param name="Role">The role of the message sender (e.g. "user", "assistant", "system").</param>
/// <param name="Content">The text content of the message.</param>
/// <param name="Iteration">The pipeline iteration number this entry belongs to, or <c>null</c> for legacy entries.</param>
/// <param name="Purpose">A short label describing the purpose of this entry (e.g. "planning", "craft-prompt", "worker-output", "error"), or <c>null</c> for legacy entries.</param>
public sealed record ConversationEntry(
    string Role,
    string Content,
    int? Iteration = null,
    string? Purpose = null);

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
