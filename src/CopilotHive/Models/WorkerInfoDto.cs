using System.Text.Json.Serialization;

namespace CopilotHive.Models;

/// <summary>
/// Describes a single connected worker for the <c>/health</c> endpoint response.
/// </summary>
public sealed class WorkerInfoDto
{
    /// <summary>Unique identifier of the worker.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Role of the worker, or <c>null</c> for generic (unspecified) workers.</summary>
    [JsonPropertyName("role")]
    public required string? Role { get; init; }

    /// <summary>Whether the worker is currently executing a task.</summary>
    [JsonPropertyName("is_busy")]
    public required bool IsBusy { get; init; }

    /// <summary>Whether this worker registered without a fixed role.</summary>
    [JsonPropertyName("is_generic")]
    public required bool IsGeneric { get; init; }

    /// <summary>Identifier of the task the worker is executing, or <c>null</c> when idle.</summary>
    [JsonPropertyName("current_task_id")]
    public required string? CurrentTaskId { get; init; }
}
