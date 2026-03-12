namespace CopilotHive.Copilot;

/// <summary>
/// Production factory that creates real Copilot SDK clients connecting over TCP.
/// </summary>
public sealed class CopilotClientFactory(string? gitHubToken) : ICopilotClientFactory
{
    public ICopilotWorkerClient Create(int port) => new CopilotWorkerClient(port, gitHubToken);
}
