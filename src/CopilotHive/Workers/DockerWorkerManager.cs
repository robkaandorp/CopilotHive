using Docker.DotNet;
using Docker.DotNet.Models;
using CopilotHive.Configuration;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<DockerWorkerManager>? _logger;
    private int _nextPort;

    /// <summary>
    /// Initialises a new <see cref="DockerWorkerManager"/> using the default local Docker daemon.
    /// </summary>
    /// <param name="config">Configuration providing the Docker image, base port, and credentials.</param>
    /// <param name="logger">Optional logger; when omitted, log output is suppressed.</param>
    public DockerWorkerManager(HiveConfiguration config, ILogger<DockerWorkerManager>? logger = null)
    {
        _config = config;
        _nextPort = config.BasePort;
        _logger = logger;
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
                _logger?.LogInformation("Cleaned up stale container: {ContainerName}", name);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("Could not remove stale container {ContainerName}: {Message}", name, ex.Message);
            }
        }
    }

    /// <summary>
    /// Spawns a new Docker container for the specified worker role and waits for it to start.
    /// </summary>
    /// <param name="role">Worker role (coder, reviewer, tester, etc.).</param>
    /// <param name="clonePath">Host path to the worker's git clone, mounted into the container.</param>
    /// <param name="agentsMdPath">Host path to the AGENTS.md directory, mounted into the container.</param>
    /// <param name="model">Model identifier passed to the worker container.</param>
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
                    $"GH_TOKEN={_config.GitHubToken}",
                    $"LLM_PROVIDER=copilot",
                ],
                HostConfig = new HostConfig
                {
                    Binds =
                    [
                        $"{Path.GetFullPath(clonePath)}:/copilot-home",
                        $"{Path.GetFullPath(agentsMdPath)}:/opt/copilot-env/AGENTS.md",
                    ],
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
        _logger?.LogInformation("Spawned {Role} worker: {WorkerId} on port {Port} (model: {Model})", role, id, port, model);
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
        _logger?.LogInformation("Stopped worker: {WorkerId}", workerId);
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
