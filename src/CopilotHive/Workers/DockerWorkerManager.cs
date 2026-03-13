using Docker.DotNet;
using Docker.DotNet.Models;
using CopilotHive.Configuration;

namespace CopilotHive.Workers;

/// <summary>
/// Manages the lifecycle of worker containers using the Docker daemon.
/// </summary>
public sealed class DockerWorkerManager : IWorkerManager
{
    private readonly DockerClient _docker;
    private readonly HiveConfiguration _config;
    private readonly Dictionary<string, WorkerInfo> _workers = [];
    private readonly string _sessionId = Guid.NewGuid().ToString("N")[..8];
    private int _nextPort;

    /// <summary>
    /// Initialises a new <see cref="DockerWorkerManager"/> using the default local Docker daemon.
    /// </summary>
    /// <param name="config">Configuration providing the Docker image, base port, and credentials.</param>
    public DockerWorkerManager(HiveConfiguration config)
    {
        _config = config;
        _nextPort = config.BasePort;
        _docker = new DockerClientConfiguration().CreateClient();
    }

    /// <summary>Read-only view of all currently tracked worker containers keyed by worker ID.</summary>
    public IReadOnlyDictionary<string, WorkerInfo> Workers => _workers;

    /// <summary>
    /// Removes any stale copilothive-* containers left over from previous runs.
    /// Called automatically before the first worker is spawned.
    /// </summary>
    public async Task CleanupStaleContainersAsync(CancellationToken ct = default)
    {
        var containers = await _docker.Containers.ListContainersAsync(
            new ContainersListParameters { All = true }, ct);

        foreach (var container in containers)
        {
            var name = container.Names.FirstOrDefault()?.TrimStart('/') ?? "";
            if (!name.StartsWith("copilothive-")) continue;

            try
            {
                await _docker.Containers.RemoveContainerAsync(
                    container.ID,
                    new ContainerRemoveParameters { Force = true },
                    ct);
                Console.WriteLine($"[Hive] Cleaned up stale container: {name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Hive] Warning: could not remove stale container {name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Spawns a new Docker container for the specified worker role and waits for it to start.
    /// </summary>
    /// <param name="role">Worker role (coder, reviewer, tester, etc.).</param>
    /// <param name="clonePath">Host path to the worker's git clone, mounted into the container.</param>
    /// <param name="agentsMdPath">Host path to the AGENTS.md directory, mounted into the container.</param>
    /// <param name="model">Model identifier passed to the Copilot CLI inside the container.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="WorkerInfo"/> describing the spawned container.</returns>
    public async Task<WorkerInfo> SpawnWorkerAsync(
        WorkerRole role,
        string clonePath,
        string agentsMdPath,
        string model,
        CancellationToken ct = default)
    {
        var port = _nextPort++;
        var id = $"copilothive-{_sessionId}-{role.ToString().ToLowerInvariant()}-{port}";

        var response = await _docker.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Image = _config.DockerImage,
                Name = id,
                Env =
                [
                    "COPILOT_MODE=headless",
                    $"COPILOT_PORT={Constants.DefaultAgentPort}",
                    $"GH_TOKEN={_config.GitHubToken}",
                    $"COPILOT_MODEL={model}",
                    "COPILOT_ALLOW_ALL=true",
                    "COPILOT_AUTO_UPDATE=true",
                ],
                ExposedPorts = new Dictionary<string, EmptyStruct>
                {
                    [$"{Constants.DefaultAgentPort}/tcp"] = default,
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
                        [$"{Constants.DefaultAgentPort}/tcp"] = [new PortBinding { HostPort = port.ToString() }],
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

    /// <summary>
    /// Stops and removes the Docker container for the specified worker.
    /// </summary>
    /// <param name="workerId">Identifier of the worker to stop.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task StopWorkerAsync(string workerId, CancellationToken ct = default)
    {
        if (!_workers.TryGetValue(workerId, out var worker))
            return;

        try
        {
            await _docker.Containers.StopContainerAsync(
                worker.ContainerId,
                new ContainerStopParameters { WaitBeforeKillSeconds = 5 },
                ct);
        }
        catch (Exception)
        {
            // Container may already be stopped
        }

        try
        {
            await _docker.Containers.RemoveContainerAsync(
                worker.ContainerId,
                new ContainerRemoveParameters { Force = true },
                ct);
        }
        catch (Exception)
        {
            // Container may already be removed
        }

        _workers.Remove(workerId);
        Console.WriteLine($"[Hive] Stopped worker: {workerId}");
    }

    /// <summary>
    /// Stops and removes all currently tracked worker containers.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task StopAllWorkersAsync(CancellationToken ct = default)
    {
        foreach (var id in _workers.Keys.ToList())
        {
            await StopWorkerAsync(id, ct);
        }
    }

    /// <summary>Stops all workers and disposes the Docker client.</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAllWorkersAsync();
        _docker.Dispose();
    }
}
