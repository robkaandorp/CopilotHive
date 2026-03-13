using GitHub.Copilot.SDK;

namespace CopilotHive.Copilot;

/// <summary>
/// Wraps the Copilot SDK to communicate with a headless worker container.
/// Each instance connects to one worker on a specific port.
/// </summary>
public sealed class CopilotWorkerClient : ICopilotWorkerClient
{
    private readonly CopilotClient _client;
    private CopilotSession? _session;
    private readonly int _port;

    /// <summary>Maximum number of connection attempts before throwing.</summary>
    public int MaxConnectRetries { get; init; } = Constants.CopilotRunnerMaxRetries;
    /// <summary>Delay between consecutive connection attempts.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(Constants.RetryDelaySeconds);

    /// <summary>
    /// Initialises a new <see cref="CopilotWorkerClient"/> that connects to the given port.
    /// </summary>
    /// <param name="port">TCP port of the worker's Copilot CLI.</param>
    /// <param name="gitHubToken">Optional GitHub token (unused; the headless server manages its own auth).</param>
    public CopilotWorkerClient(int port, string? gitHubToken = null)
    {
        _port = port;
        // Don't pass GitHubToken — the external headless server manages its own auth
        _client = new CopilotClient(new CopilotClientOptions
        {
            CliUrl = $"localhost:{port}",
            AutoStart = false,
        });
    }

    /// <summary>
    /// Connects to the worker's Copilot CLI, retrying up to <see cref="MaxConnectRetries"/> times.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        // Wait for the container to boot before first connection attempt
        await Task.Delay(TimeSpan.FromSeconds(Constants.WorkerBootDelaySeconds), ct);

        await _client.StartAsync();
        Exception? lastException = null;

        for (var attempt = 1; attempt <= MaxConnectRetries; attempt++)
        {
            try
            {
                _session = await _client.CreateSessionAsync(new SessionConfig
                {
                    Streaming = false,
                    OnPermissionRequest = PermissionHandler.ApproveAll,
                });
                Console.WriteLine($"[SDK] Connected to worker on port {_port} (attempt {attempt})");
                return;
            }
            catch (Exception ex) when (attempt < MaxConnectRetries)
            {
                lastException = ex;
                Console.WriteLine($"[SDK] Connection attempt {attempt}/{MaxConnectRetries} to port {_port} failed: {ex.Message}");
                await Task.Delay(RetryDelay, ct);
            }
        }

        throw new CopilotWorkerException(
            $"Failed to connect to worker on port {_port} after {MaxConnectRetries} attempts: {lastException?.Message}");
    }

    /// <summary>
    /// Send a task prompt to the worker and wait for the complete response.
    /// </summary>
    public async Task<string> SendTaskAsync(string prompt, CancellationToken ct = default)
    {
        if (_session is null)
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

        var done = new TaskCompletionSource<string>();
        string response = "";

        using var subscription = _session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageEvent msg:
                    response = msg.Data.Content;
                    break;
                case SessionIdleEvent:
                    done.TrySetResult(response);
                    break;
                case SessionErrorEvent err:
                    done.TrySetException(new CopilotWorkerException(err.Data.Message));
                    break;
            }
        });

        using var ctReg = ct.Register(() => done.TrySetCanceled());

        await _session.SendAsync(new MessageOptions { Prompt = prompt });
        return await done.Task;
    }

    /// <summary>Disposes the active Copilot session and stops the underlying client.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
            await _session.DisposeAsync();

        await _client.StopAsync();
    }
}

/// <summary>Represents an error raised by <see cref="CopilotWorkerClient"/>.</summary>
public sealed class CopilotWorkerException(string message) : Exception(message);
