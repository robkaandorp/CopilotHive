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

    /// <summary>Identifier of the task the worker is executing, or <c>null</c> when idle.</summary>
    [JsonPropertyName("current_task_id")]
    public required string? CurrentTaskId { get; init; }

    /// <summary>
    /// Whether the worker was available at the captured instant: not busy, carrying no task, holding
    /// no completion publication and not awaiting its own accepted Ready — i.e. eligible for the
    /// checked pool claim's own state predicate. Defaults to <c>false</c>.
    /// </summary>
    /// <remarks>
    /// An OBSERVATION, NOT A RESERVATION AND NOT A DELIVERY GUARANTEE: the worker may be claimed or
    /// withheld before any dispatcher acts on it.
    /// </remarks>
    [JsonPropertyName("is_available")]
    public bool IsAvailable { get; init; }

    /// <summary>
    /// Whether the worker was awaiting its own accepted Ready at the captured instant — a non-busy
    /// worker that is nevertheless withheld from selection. Defaults to <c>false</c>.
    /// </summary>
    /// <remarks>An OBSERVATION ONLY, carrying no reservation and no delivery guarantee.</remarks>
    [JsonPropertyName("awaiting_worker_ready")]
    public bool AwaitingWorkerReady { get; init; }
}
