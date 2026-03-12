using System.Threading.Channels;
using CopilotHive.Shared.Grpc;

namespace CopilotHive.Services;

public sealed class ConnectedWorker
{
    public required string Id { get; init; }
    public required WorkerRole Role { get; init; }
    public required string[] Capabilities { get; init; }
    public bool IsBusy { get; set; }
    public string? CurrentTaskId { get; set; }
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
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
