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

    /// <summary>
    /// Number of workers NOT currently executing a task. This INCLUDES workers withheld from
    /// selection (awaiting their own accepted Ready, or still publishing a completion), so it is NOT
    /// a count of assignable capacity — see <see cref="AvailableWorkers"/> for that.
    /// </summary>
    [JsonPropertyName("idle_workers")]
    public required int IdleWorkers { get; init; }

    /// <summary>Number of workers currently executing a task.</summary>
    [JsonPropertyName("busy_workers")]
    public required int BusyWorkers { get; init; }

    /// <summary>
    /// Number of workers available at the captured instant: not busy, carrying no task, holding no
    /// completion publication and not awaiting their own accepted Ready — i.e. eligible for the
    /// checked pool claim's own state predicate. Defaults to <c>0</c> for existing construction sites.
    /// </summary>
    /// <remarks>
    /// An OBSERVATION, NOT A RESERVATION AND NOT A DELIVERY GUARANTEE: a worker counted here may be
    /// claimed or withheld by the time a dispatcher acts.
    /// </remarks>
    [JsonPropertyName("available_workers")]
    public int AvailableWorkers { get; init; }

    /// <summary>
    /// Number of workers awaiting their own accepted Ready at the captured instant — the
    /// longer-withheld members of <see cref="IdleWorkers"/>, unavailable for assignment. Defaults to
    /// <c>0</c> for existing construction sites.
    /// </summary>
    /// <remarks>An OBSERVATION ONLY, carrying no reservation and no delivery guarantee.</remarks>
    [JsonPropertyName("awaiting_ready_workers")]
    public int AwaitingReadyWorkers { get; init; }

    /// <summary>Per-worker details.</summary>
    [JsonPropertyName("workers")]
    public required List<WorkerInfoDto> Workers { get; init; }
}
