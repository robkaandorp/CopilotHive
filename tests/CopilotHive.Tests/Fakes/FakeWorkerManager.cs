using CopilotHive.Workers;

namespace CopilotHive.Tests.Fakes;

/// <summary>
/// Fake worker manager that tracks spawned/stopped workers without Docker.
/// </summary>
public sealed class FakeWorkerManager : IWorkerManager
{
    private readonly Dictionary<string, WorkerInfo> _workers = [];
    private int _nextPort = 9000;

    public IReadOnlyDictionary<string, WorkerInfo> Workers => _workers;
    public List<(WorkerRole Role, string ClonePath, string Model)> SpawnHistory { get; } = [];
    public List<string> StopHistory { get; } = [];

    public Task<WorkerInfo> SpawnWorkerAsync(
        WorkerRole role, string clonePath, string agentsMdPath, string model, CancellationToken ct = default)
    {
        var port = _nextPort++;
        var id = $"fake-{role.ToString().ToLowerInvariant()}-{port}";

        var worker = new WorkerInfo
        {
            Id = id,
            Role = role,
            ContainerId = $"container-{id}",
            Port = port,
            ClonePath = clonePath,
            AgentsMdPath = agentsMdPath,
        };

        _workers[id] = worker;
        SpawnHistory.Add((role, clonePath, model));
        return Task.FromResult(worker);
    }

    public Task StopWorkerAsync(string workerId, CancellationToken ct = default)
    {
        _workers.Remove(workerId);
        StopHistory.Add(workerId);
        return Task.CompletedTask;
    }

    public Task StopAllWorkersAsync(CancellationToken ct = default)
    {
        foreach (var id in _workers.Keys.ToList())
            _workers.Remove(id);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _workers.Clear();
        return ValueTask.CompletedTask;
    }
}
