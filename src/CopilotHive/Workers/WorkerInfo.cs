namespace CopilotHive.Workers;

/// <summary>
/// Describes a worker container spawned by the <see cref="IWorkerManager"/>.
/// </summary>
public sealed class WorkerInfo
{
    /// <summary>Unique identifier for this worker instance.</summary>
    public required string Id { get; init; }
    /// <summary>Role this worker is performing.</summary>
    public required WorkerRole Role { get; init; }
    /// <summary>Docker container ID of the running worker container.</summary>
    public required string ContainerId { get; set; }
    /// <summary>Host TCP port mapped to the worker's Copilot CLI port (8000 inside the container).</summary>
    public required int Port { get; init; }
    /// <summary>Host path to the worker's git workspace clone, mounted into the container.</summary>
    public required string ClonePath { get; init; }
    /// <summary>Host path to the AGENTS.md directory, mounted into the container.</summary>
    public required string AgentsMdPath { get; init; }
}
