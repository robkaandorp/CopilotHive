using CopilotHive.Copilot;

namespace CopilotHive.Tests.Fakes;

/// <summary>
/// Fake Copilot client that returns scripted responses based on prompt content.
/// </summary>
public sealed class FakeCopilotClient : ICopilotWorkerClient
{
    private readonly Func<string, string> _responder;
    private readonly List<string> _prompts = [];

    public IReadOnlyList<string> ReceivedPrompts => _prompts;
    public bool Connected { get; private set; }
    public bool Disposed { get; private set; }

    public FakeCopilotClient(Func<string, string> responder)
    {
        _responder = responder;
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        Connected = true;
        return Task.CompletedTask;
    }

    public Task<string> SendTaskAsync(string prompt, CancellationToken ct = default)
    {
        _prompts.Add(prompt);
        return Task.FromResult(_responder(prompt));
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Factory that creates FakeCopilotClient instances with a shared responder.
/// Tracks all created clients for test assertions.
/// </summary>
public sealed class FakeCopilotClientFactory : ICopilotClientFactory
{
    private readonly Func<string, string> _responder;
    private readonly List<FakeCopilotClient> _clients = [];

    public IReadOnlyList<FakeCopilotClient> CreatedClients => _clients;

    public FakeCopilotClientFactory(Func<string, string> responder)
    {
        _responder = responder;
    }

    public ICopilotWorkerClient Create(int port)
    {
        var client = new FakeCopilotClient(_responder);
        _clients.Add(client);
        return client;
    }
}
