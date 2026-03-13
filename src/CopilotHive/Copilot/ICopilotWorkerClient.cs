namespace CopilotHive.Copilot;

/// <summary>
/// Factory that creates Copilot client connections to workers.
/// </summary>
public interface ICopilotClientFactory
{
    /// <summary>
    /// Creates a new client that connects to the worker on the given port.
    /// </summary>
    /// <param name="port">TCP port the worker is listening on.</param>
    /// <returns>A new <see cref="ICopilotWorkerClient"/> instance.</returns>
    ICopilotWorkerClient Create(int port);
}

/// <summary>
/// Communicates with a single Copilot worker.
/// </summary>
public interface ICopilotWorkerClient : IAsyncDisposable
{
    /// <summary>
    /// Establishes a session with the worker's Copilot CLI.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Sends a prompt to the worker and returns the complete response text.
    /// </summary>
    /// <param name="prompt">The prompt to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The worker's response text.</returns>
    Task<string> SendTaskAsync(string prompt, CancellationToken ct = default);
}
