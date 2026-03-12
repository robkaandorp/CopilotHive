using System.Collections.Concurrent;
using CopilotHive.Shared.Grpc;

namespace CopilotHive.Services;

public sealed class WorkerPool
{
    private readonly ConcurrentDictionary<string, ConnectedWorker> _workers = new();

    public ConnectedWorker RegisterWorker(string id, WorkerRole role, string[] capabilities)
    {
        var worker = new ConnectedWorker
        {
            Id = id,
            Role = role,
            Capabilities = capabilities,
        };

        if (!_workers.TryAdd(id, worker))
            throw new InvalidOperationException($"Worker '{id}' is already registered.");

        return worker;
    }

    public bool RemoveWorker(string id)
    {
        if (!_workers.TryRemove(id, out var worker))
            return false;

        worker.MessageChannel.Writer.TryComplete();
        return true;
    }

    public ConnectedWorker? GetIdleWorker(WorkerRole role)
    {
        foreach (var kvp in _workers)
        {
            var w = kvp.Value;
            if (w.Role == role && !w.IsBusy)
                return w;
        }

        return null;
    }

    public IReadOnlyList<ConnectedWorker> GetAllWorkers() =>
        _workers.Values.ToList().AsReadOnly();

    public ConnectedWorker? GetWorker(string id) =>
        _workers.GetValueOrDefault(id);

    public void UpdateHeartbeat(string id)
    {
        if (_workers.TryGetValue(id, out var worker))
            worker.LastHeartbeat = DateTime.UtcNow;
    }

    public void MarkBusy(string id, string taskId)
    {
        if (_workers.TryGetValue(id, out var worker))
        {
            worker.IsBusy = true;
            worker.CurrentTaskId = taskId;
        }
    }

    public void MarkIdle(string id)
    {
        if (_workers.TryGetValue(id, out var worker))
        {
            worker.IsBusy = false;
            worker.CurrentTaskId = null;
        }
    }
}
