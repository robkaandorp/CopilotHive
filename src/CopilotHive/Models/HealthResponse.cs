using System.Text.Json.Serialization;

namespace CopilotHive.Models;

/// <summary>
/// Structured response body returned by the <c>GET /health</c> endpoint.
/// </summary>
public sealed class HealthResponse
{
    /// <summary>Human-readable health status; always <c>"Healthy"</c> when the server is up.</summary>
    public string Status { get; init; } = "Healthy";

    /// <summary>Human-readable uptime string, e.g. <c>"2d 3h 14m"</c>.</summary>
    public string Uptime { get; init; } = string.Empty;

    /// <summary>Raw uptime as a <see cref="TimeSpan"/> for easy programmatic parsing.</summary>
    public TimeSpan UptimeSpan { get; init; }

    /// <summary>Number of goals currently in <c>Pending</c> or <c>InProgress</c> state.</summary>
    public int ActiveGoals { get; init; }

    /// <summary>Number of goals that have reached <c>Completed</c> state.</summary>
    public int CompletedGoals { get; init; }

    /// <summary>Number of worker processes currently registered with the hive.</summary>
    public int ConnectedWorkers { get; init; }

    /// <summary>Assembly informational version string, e.g. <c>"1.0.0.0"</c>.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>UTC timestamp when this response was generated.</summary>
    public DateTime ServerTime { get; init; }

    /// <summary>Monotonically increasing counter of how many times <c>/health</c> has been called.</summary>
    public int CheckNumber { get; init; }

    /// <summary>
    /// Live worker pool statistics including per-worker details.
    /// Nested under <c>worker_pool</c> to keep the top-level response clean.
    /// </summary>
    [JsonPropertyName("worker_pool")]
    public WorkerPoolStatsDto? WorkerPool { get; init; }
}
