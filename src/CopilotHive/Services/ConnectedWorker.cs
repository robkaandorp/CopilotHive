using System.Threading.Channels;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;

namespace CopilotHive.Services;

/// <summary>
/// Represents a worker that is currently connected to the orchestrator via gRPC.
/// Holds worker metadata and the channel used to push messages to it.
/// </summary>
public sealed class ConnectedWorker
{
    /// <summary>Unique identifier assigned to this worker.</summary>
    public required string Id { get; init; }
    /// <summary>Current role of this worker. Initially Unspecified; updated dynamically per task.</summary>
    public required Workers.WorkerRole Role { get; set; }
    /// <summary>Capabilities advertised by this worker during registration.</summary>
    public required string[] Capabilities { get; init; }
    /// <summary>Whether the worker is currently executing a task.</summary>
    public bool IsBusy { get; set; }
    /// <summary>Identifier of the task the worker is currently executing, or <c>null</c> when idle.</summary>
    public string? CurrentTaskId { get; set; }
    /// <summary>
    /// UTC timestamp when the worker started its current task, or <c>null</c> when idle.
    /// For display/statistics only. Stale detection uses <see cref="LastActivityAt"/>.
    /// </summary>
    public DateTime? CurrentTaskStartedAt { get; set; }
    /// <summary>UTC timestamp of the last task-specific stream activity (ToolRequest, Progress, or Complete message). NOT updated by Ready messages or heartbeats. Used for inactivity-based stale detection. A silently processing worker will time out — intentional, as it's indistinguishable from a hung call.</summary>
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
    /// <summary>UTC timestamp of the last heartbeat received from this worker.</summary>
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
    /// <summary>UTC timestamp when this worker first connected.</summary>
    public DateTime ConnectedAt { get; init; } = DateTime.UtcNow;
    /// <summary>Model used for the current task, or <c>null</c> when idle.</summary>
    public string? CurrentModel { get; set; }
    /// <summary>Estimated context window usage as a percentage (0–100), or 0 when idle.</summary>
    public int ContextUsagePercent { get; set; }

    /// <summary>
    /// The orchestrator writes messages here; the worker's stream reads from it.
    /// </summary>
    public Channel<OrchestratorMessage> MessageChannel { get; } =
        Channel.CreateUnbounded<OrchestratorMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
}
