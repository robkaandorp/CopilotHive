namespace CopilotHive.Workers;

/// <summary>
/// Manages the lifecycle of worker containers/processes.
/// </summary>
public interface IWorkerManager : IAsyncDisposable
{
    IReadOnlyDictionary<string, WorkerInfo> Workers { get; }

    Task<WorkerInfo> SpawnWorkerAsync(
        WorkerRole role,
        string clonePath,
        string agentsMdPath,
        string model,
        CancellationToken ct = default);

    Task StopWorkerAsync(string workerId, CancellationToken ct = default);

    Task StopAllWorkersAsync(CancellationToken ct = default);
}
