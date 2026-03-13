using System.Threading.Channels;
using CopilotHive.Shared.Grpc;

namespace CopilotHive.Services;

/// <summary>
/// Represents a worker that is currently connected to the orchestrator via gRPC.
/// Holds worker metadata and the channel used to push messages to it.
/// </summary>
public sealed class ConnectedWorker
{
    /// <summary>Unique identifier assigned to this worker.</summary>
    public required string Id { get; init; }
    /// <summary>Role of this worker (coder, reviewer, tester, etc.).</summary>
    public required WorkerRole Role { get; init; }
    /// <summary>Capabilities advertised by this worker during registration.</summary>
    public required string[] Capabilities { get; init; }
    /// <summary>Whether the worker is currently executing a task.</summary>
    public bool IsBusy { get; set; }
    /// <summary>Identifier of the task the worker is currently executing, or <c>null</c> when idle.</summary>
    public string? CurrentTaskId { get; set; }
    /// <summary>UTC timestamp of the last heartbeat received from this worker.</summary>
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
    /// <summary>UTC timestamp when this worker first connected.</summary>
    public DateTime ConnectedAt { get; init; } = DateTime.UtcNow;

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
