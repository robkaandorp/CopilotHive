namespace CopilotHive.Workers;

public sealed class WorkerInfo
{
    public required string Id { get; init; }
    public required WorkerRole Role { get; init; }
    public required string ContainerId { get; set; }
    public required int Port { get; init; }
    public required string ClonePath { get; init; }
    public required string AgentsMdPath { get; init; }
}
