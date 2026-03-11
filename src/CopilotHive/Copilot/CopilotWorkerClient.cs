using GitHub.Copilot.SDK;

namespace CopilotHive.Copilot;

/// <summary>
/// Wraps the Copilot SDK to communicate with a headless worker container.
/// Each instance connects to one worker on a specific port.
/// </summary>
public sealed class CopilotWorkerClient : IAsyncDisposable
{
    private readonly CopilotClient _client;
    private CopilotSession? _session;

    public CopilotWorkerClient(int port, string model, string? gitHubToken = null)
    {
        _client = new CopilotClient(new CopilotClientOptions
        {
            CliUrl = $"localhost:{port}",
            AutoStart = false,
            GitHubToken = gitHubToken,
        });
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _client.StartAsync();
        _session = await _client.CreateSessionAsync(new SessionConfig
        {
            Streaming = false,
        });
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

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
            await _session.DisposeAsync();

        await _client.StopAsync();
    }
}

public sealed class CopilotWorkerException(string message) : Exception(message);
