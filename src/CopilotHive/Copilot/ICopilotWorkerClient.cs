namespace CopilotHive.Copilot;

/// <summary>
/// Factory that creates Copilot client connections to workers.
/// </summary>
public interface ICopilotClientFactory
{
    ICopilotWorkerClient Create(int port);
}

/// <summary>
/// Communicates with a single Copilot worker.
/// </summary>
public interface ICopilotWorkerClient : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken ct = default);
    Task<string> SendTaskAsync(string prompt, CancellationToken ct = default);
}
