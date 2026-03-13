namespace CopilotHive.Copilot;

/// <summary>
/// Production factory that creates real Copilot SDK clients connecting over TCP.
/// </summary>
public sealed class CopilotClientFactory(string? gitHubToken) : ICopilotClientFactory
{
    /// <summary>
    /// Creates a new <see cref="ICopilotWorkerClient"/> connected to the worker on the given port.
    /// </summary>
    /// <param name="port">TCP port the worker's Copilot CLI is listening on.</param>
    /// <returns>A new client instance; caller is responsible for disposing it.</returns>
    public ICopilotWorkerClient Create(int port) => new CopilotWorkerClient(port, gitHubToken);
}
