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
    private CustomAgentConfig? _customAgent;

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

    /// <summary>
    /// Sets the custom agent configuration for this worker's role.
    /// Applied on the next session creation (ConnectAsync or ResetSessionAsync).
    /// </summary>
    public void SetCustomAgent(string role, string agentsMdContent)
    {
        _customAgent = new CustomAgentConfig
        {
            Name = role,
            DisplayName = $"CopilotHive {char.ToUpperInvariant(role[0])}{role[1..]}",
            Description = $"CopilotHive worker agent for the {role} role",
            Prompt = agentsMdContent,
            Tools = null, // all tools available for workers
        };
        Console.WriteLine($"[Copilot] Custom agent set for role '{role}' ({agentsMdContent.Length} chars)");
    }

    private SessionConfig BuildSessionConfig() => new()
    {
        Streaming = false,
        OnPermissionRequest = PermissionHandler.ApproveAll,
        CustomAgents = _customAgent is not null ? [_customAgent] : [],
    };

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _client.StartAsync();
        Exception? lastException = null;

        for (var attempt = 1; attempt <= MaxConnectRetries; attempt++)
        {
            try
            {
                _session = await _client.CreateSessionAsync(BuildSessionConfig());
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
    /// Dispose the current session and create a fresh one, ensuring no context leaks between tasks.
    /// The new session picks up the latest CustomAgent configuration.
    /// </summary>
    public async Task ResetSessionAsync(CancellationToken ct = default)
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }

        _session = await _client.CreateSessionAsync(BuildSessionConfig());

        Console.WriteLine($"[Copilot] Session reset (localhost:{_port}, agent={_customAgent?.Name ?? "none"})");
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
