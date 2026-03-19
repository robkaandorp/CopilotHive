using System.Text.Json.Serialization;

namespace CopilotHive.Models;

/// <summary>
/// Live worker pool statistics returned as part of the <c>/health</c> endpoint response.
/// Uses snake_case JSON property names per the API contract.
/// </summary>
public sealed class WorkerPoolStatsDto
{
    /// <summary>Total number of registered workers.</summary>
    [JsonPropertyName("total_workers")]
    public required int TotalWorkers { get; init; }

    /// <summary>Number of workers currently idle and available for tasks.</summary>
    [JsonPropertyName("idle_workers")]
    public required int IdleWorkers { get; init; }

    /// <summary>Number of workers currently executing a task.</summary>
    [JsonPropertyName("busy_workers")]
    public required int BusyWorkers { get; init; }

    /// <summary>Per-worker details.</summary>
    [JsonPropertyName("workers")]
    public required List<WorkerInfoDto> Workers { get; init; }
}
