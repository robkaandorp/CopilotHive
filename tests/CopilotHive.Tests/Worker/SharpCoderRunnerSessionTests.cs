extern alias WorkerAssembly;

using Microsoft.Extensions.AI;
using SharpCoder;
using WorkerAssembly::CopilotHive.Worker;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Unit tests for <see cref="SharpCoderRunner"/> session management:
/// <see cref="SharpCoderRunner.SetSession"/> and <see cref="SharpCoderRunner.GetSession"/>.
/// </summary>
public sealed class SharpCoderRunnerSessionTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Creates a <see cref="SharpCoderRunner"/> with a stub client suitable for unit tests.</summary>
    private static SharpCoderRunner CreateRunner()
        => new(new StubChatClientForSession(), "test-model");

    // ── SetSession / GetSession ───────────────────────────────────────────────

    /// <summary>
    /// <see cref="SharpCoderRunner.GetSession"/> must return <c>null</c> when no session has been
    /// set on a freshly constructed runner.
    /// </summary>
    [Fact]
    public void GetSession_BeforeSetSession_ReturnsNull()
    {
        var runner = CreateRunner();

        var session = runner.GetSession();

        Assert.Null(session);
    }

    /// <summary>
    /// <see cref="SharpCoderRunner.SetSession"/> with a valid <see cref="AgentSession"/> must cause
    /// <see cref="SharpCoderRunner.GetSession"/> to return the same instance.
    /// </summary>
    [Fact]
    public void SetSession_WithAgentSession_GetSessionReturnsSameInstance()
    {
        var runner = CreateRunner();
        var session = AgentSession.Create("test-session-id");

        runner.SetSession(session);
        var returned = runner.GetSession();

        Assert.Same(session, returned);
    }

    /// <summary>
    /// <see cref="SharpCoderRunner.SetSession"/> with <c>null</c> must cause
    /// <see cref="SharpCoderRunner.GetSession"/> to return <c>null</c>.
    /// </summary>
    [Fact]
    public void SetSession_WithNull_GetSessionReturnsNull()
    {
        var runner = CreateRunner();
        var session = AgentSession.Create("to-be-cleared");
        runner.SetSession(session);

        runner.SetSession(null);
        var returned = runner.GetSession();

        Assert.Null(returned);
    }

    /// <summary>
    /// <see cref="SharpCoderRunner.SetSession"/> with a non-<see cref="AgentSession"/> object must
    /// silently ignore it and <see cref="SharpCoderRunner.GetSession"/> must return <c>null</c>.
    /// </summary>
    [Fact]
    public void SetSession_WithNonAgentSession_GetSessionReturnsNull()
    {
        var runner = CreateRunner();

        runner.SetSession("not an AgentSession");
        var returned = runner.GetSession();

        Assert.Null(returned);
    }

    /// <summary>
    /// After <see cref="SharpCoderRunner.SendPromptAsync"/> completes, a session must have been
    /// created so that <see cref="SharpCoderRunner.GetSession"/> returns a non-null value.
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_WithNoPreExistingSession_CreatesSession()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"session-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var runner = CreateRunner();

            await runner.SendPromptAsync("do something", workDir, TestContext.Current.CancellationToken);
            var session = runner.GetSession();

            Assert.NotNull(session);
            Assert.IsType<AgentSession>(session);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// When a pre-existing <see cref="AgentSession"/> is set before <see cref="SharpCoderRunner.SendPromptAsync"/>,
    /// the same session instance must be returned from <see cref="SharpCoderRunner.GetSession"/> afterward
    /// (the session is updated in-place, not replaced).
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_WithPreExistingSession_RetainsSession()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"session-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var runner = CreateRunner();
            var existingSession = AgentSession.Create("pre-existing");

            runner.SetSession(existingSession);
            await runner.SendPromptAsync("do something", workDir, TestContext.Current.CancellationToken);

            var returnedSession = runner.GetSession();
            Assert.NotNull(returnedSession);
            // The same instance should be returned (session updated in place)
            Assert.Same(existingSession, returnedSession);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// <see cref="SharpCoderRunner.ResetSessionAsync"/> must clear <c>_session</c> to <c>null</c>
    /// so that subsequent prompts start with a fresh session instead of leaking stale context.
    /// </summary>
    [Fact]
    public async Task ResetSessionAsync_ClearsSession()
    {
        var runner = CreateRunner();
        var session = AgentSession.Create("session-to-clear");

        // Pre-condition: session is set
        runner.SetSession(session);
        Assert.NotNull(runner.GetSession());

        // Act
        await runner.ResetSessionAsync(ct: TestContext.Current.CancellationToken);

        // Assert: session must be null after reset
        Assert.Null(runner.GetSession());
    }
}

/// <summary>
/// Minimal <see cref="IChatClient"/> stub for session tests — returns a single assistant message
/// and signals <see cref="ChatFinishReason.Stop"/> so <see cref="SharpCoder.CodingAgent"/> terminates.
/// </summary>
file sealed class StubChatClientForSession : IChatClient
{
    /// <inheritdoc />
    public ChatClientMetadata Metadata => new("stub-session", null, "stub-model");

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."))
        {
            FinishReason = ChatFinishReason.Stop,
        };
        return Task.FromResult(response);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose() { }
}
