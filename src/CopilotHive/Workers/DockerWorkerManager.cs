using Docker.DotNet;
using Docker.DotNet.Models;
using CopilotHive.Configuration;

namespace CopilotHive.Workers;

public sealed class DockerWorkerManager : IWorkerManager
{
    private readonly DockerClient _docker;
    private readonly HiveConfiguration _config;
    private readonly Dictionary<string, WorkerInfo> _workers = [];
    private int _nextPort;

    public DockerWorkerManager(HiveConfiguration config)
    {
        _config = config;
        _nextPort = config.BasePort;
        _docker = new DockerClientConfiguration().CreateClient();
    }

    public IReadOnlyDictionary<string, WorkerInfo> Workers => _workers;

    public async Task<WorkerInfo> SpawnWorkerAsync(
        WorkerRole role,
        string clonePath,
        string agentsMdPath,
        string model,
        CancellationToken ct = default)
    {
        var port = _nextPort++;
        var id = $"copilothive-{role.ToString().ToLowerInvariant()}-{port}";

        var response = await _docker.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Image = _config.DockerImage,
                Name = id,
                Env =
                [
                    "COPILOT_MODE=headless",
                    $"COPILOT_PORT=8000",
                    $"GH_TOKEN={_config.GitHubToken}",
                    $"COPILOT_MODEL={model}",
                    "COPILOT_ALLOW_ALL=true",
                    "COPILOT_AUTO_UPDATE=true",
                ],
                ExposedPorts = new Dictionary<string, EmptyStruct>
                {
                    ["8000/tcp"] = default,
                },
                HostConfig = new HostConfig
                {
                    Binds =
                    [
                        $"{Path.GetFullPath(clonePath)}:/copilot-home",
                        $"{Path.GetFullPath(agentsMdPath)}:/opt/copilot-env/AGENTS.md",
                    ],
                    PortBindings = new Dictionary<string, IList<PortBinding>>
                    {
                        ["8000/tcp"] = [new PortBinding { HostPort = port.ToString() }],
                    },
                },
            },
            ct);

        await _docker.Containers.StartContainerAsync(response.ID, null, ct);

        var worker = new WorkerInfo
        {
            Id = id,
            Role = role,
            ContainerId = response.ID,
            Port = port,
            ClonePath = clonePath,
            AgentsMdPath = agentsMdPath,
        };

        _workers[id] = worker;
        Console.WriteLine($"[Hive] Spawned {role} worker: {id} on port {port} (model: {model})");
        return worker;
    }

    public async Task StopWorkerAsync(string workerId, CancellationToken ct = default)
    {
        if (!_workers.TryGetValue(workerId, out var worker))
            return;

        await _docker.Containers.StopContainerAsync(
            worker.ContainerId,
            new ContainerStopParameters { WaitBeforeKillSeconds = 5 },
            ct);

        await _docker.Containers.RemoveContainerAsync(
            worker.ContainerId,
            new ContainerRemoveParameters { Force = true },
            ct);

        _workers.Remove(workerId);
        Console.WriteLine($"[Hive] Stopped worker: {workerId}");
    }

    public async Task StopAllWorkersAsync(CancellationToken ct = default)
    {
        foreach (var id in _workers.Keys.ToList())
        {
            await StopWorkerAsync(id, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllWorkersAsync();
        _docker.Dispose();
    }
}
