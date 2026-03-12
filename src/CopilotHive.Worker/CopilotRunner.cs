using GitHub.Copilot.SDK;

namespace CopilotHive.Worker;

/// <summary>
/// Communicates with the Copilot CLI running in headless mode via the GitHub.Copilot.SDK.
/// The Copilot CLI is already running (started by entrypoint.sh); this class connects to it.
/// </summary>
public sealed class CopilotRunner : IAsyncDisposable
{
    private readonly CopilotClient _client;
    private CopilotSession? _session;
    private readonly int _port;

    public int MaxConnectRetries { get; init; } = 12;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    public CopilotRunner(int port = 8000)
    {
        _port = port;
        _client = new CopilotClient(new CopilotClientOptions
        {
            CliUrl = $"localhost:{port}",
            AutoStart = false,
        });
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
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
                Console.WriteLine($"[Copilot] Connected to Copilot CLI on port {_port} (attempt {attempt})");
                return;
            }
            catch (Exception ex) when (attempt < MaxConnectRetries)
            {
                lastException = ex;
                Console.WriteLine($"[Copilot] Connection attempt {attempt}/{MaxConnectRetries} failed: {ex.Message}");
                await Task.Delay(RetryDelay, ct);
            }
        }

        throw new CopilotRunnerException(
            $"Failed to connect to Copilot CLI on port {_port} after {MaxConnectRetries} attempts: {lastException?.Message}");
    }

    /// <summary>
    /// Send a prompt to the Copilot CLI and return the response text.
    /// </summary>
    public async Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
    {
        if (_session is null)
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

        Console.WriteLine($"[Copilot] Sending prompt ({prompt.Length} chars) to localhost:{_port}");

        var done = new TaskCompletionSource<string>();
        var response = "";

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
                    done.TrySetException(new CopilotRunnerException(err.Data.Message));
                    break;
            }
        });

        using var ctReg = ct.Register(() => done.TrySetCanceled());

        await _session.SendAsync(new MessageOptions { Prompt = prompt });
        var result = await done.Task;

        Console.WriteLine($"[Copilot] Received response ({result.Length} chars)");
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
            await _session.DisposeAsync();

        await _client.StopAsync();
    }
}

public sealed class CopilotRunnerException(string message) : Exception(message);
