using System.Reflection;
using System.Text.Json;
using CopilotHive;
using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SharpCoder;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Tests for the manual "Compact Session" feature on the Composer.
/// Covers the <see cref="Composer.CompactSessionAsync"/> method and the
/// <c>POST /api/composer/compact</c> REST endpoint.
/// </summary>
public sealed class ComposerCompactTests
{
    // ── Helpers ──

    /// <summary>
    /// Uses reflection to inject a fake <see cref="IChatClient"/> into a
    /// <see cref="Composer"/> instance and then rebuilds its internal
    /// <c>CodingAgent</c> by calling the private <c>RecreateAgent()</c> method.
    /// Call this BEFORE <c>SendMessage</c> — no <c>ConnectAsync</c> call is needed.
    /// </summary>
    private static void InjectFakeChatClient(Composer composer, IChatClient fakeClient)
    {
        var composerType = typeof(Composer);

        var chatClientField = composerType.GetField("_chatClient",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_chatClient field not found on Composer");
        chatClientField.SetValue(composer, fakeClient);

        var recreateAgent = composerType.GetMethod("RecreateAgent",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RecreateAgent method not found on Composer");
        recreateAgent.Invoke(composer, null);
    }

    /// <summary>
    /// Gets the private <c>_session</c> field from a <see cref="Composer"/> instance.
    /// </summary>
    private static AgentSession GetSession(Composer composer)
    {
        var sessionField = typeof(Composer).GetField("_session",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_session field not found on Composer");
        return (AgentSession)sessionField.GetValue(composer)!;
    }

    /// <summary>
    /// Gets the private <c>_isStreaming</c> field value from a <see cref="Composer"/>.
    /// </summary>
    private static bool GetIsStreaming(Composer composer)
    {
        var field = typeof(Composer).GetField("_isStreaming",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_isStreaming field not found on Composer");
        return (bool)field.GetValue(composer)!;
    }

    /// <summary>
    /// Sets the private <c>_isStreaming</c> field on a <see cref="Composer"/>.
    /// </summary>
    private static void SetIsStreaming(Composer composer, bool value)
    {
        var field = typeof(Composer).GetField("_isStreaming",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_isStreaming field not found on Composer");
        field.SetValue(composer, value);
    }

    /// <summary>
    /// Creates a standalone <see cref="Composer"/> with a mock chat client that returns
    /// a summary response from <see cref="IChatClient.GetResponseAsync"/>. The Composer
    /// is NOT connected (no <see cref="Composer.ConnectAsync"/> call) — use
    /// <see cref="InjectFakeChatClient"/> to wire up the agent.
    /// </summary>
    private static Composer CreateComposerWithMockSummaryClient(out Mock<IChatClient> mockClient)
    {
        var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);

        mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary of conversation")));

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            store,
            stateDir: tmpDir);

        return composer;
    }

    /// <summary>
    /// Adds <paramref name="count"/> alternating user/assistant messages plus a single
    /// system message at the start of the session's message history.
    /// </summary>
    private static void PopulateSession(AgentSession session, int count)
    {
        session.MessageHistory.Clear();
        session.MessageHistory.Add(new ChatMessage(ChatRole.System, "You are a helpful assistant."));
        for (var i = 0; i < count; i++)
        {
            session.MessageHistory.Add(new ChatMessage(
                i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                $"Message {i}"));
        }
    }

    // ── 1. CompactSessionAsync_WhenNotConnected_ThrowsInvalidOperationException ──

    [Fact]
    public async Task CompactSessionAsync_WhenNotConnected_ThrowsInvalidOperationException()
    {
        var composer = CreateComposerWithMockSummaryClient(out _);

        // Do NOT call ConnectAsync or InjectFakeChatClient — _agent is null.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => composer.CompactSessionAsync(TestContext.Current.CancellationToken));

        Assert.Contains("not connected", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── 2. CompactSessionAsync_WhileStreaming_ThrowsInvalidOperationException ──

    [Fact]
    public async Task CompactSessionAsync_WhileStreaming_ThrowsInvalidOperationException()
    {
        var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            // Create a Composer whose AI client throws on both streaming and non-streaming paths.
            var overflowEx = new InvalidOperationException("model_max_prompt_tokens_exceeded");
            var mockClient = new Mock<IChatClient>();
            mockClient
                .Setup(c => c.GetStreamingResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .Throws(overflowEx);
            mockClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(overflowEx);

            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                store,
                stateDir: tmpDir);

            InjectFakeChatClient(composer, mockClient.Object);

            // Trigger streaming — it will fail quickly due to the throwing client.
            composer.SendMessage("test");

            // Wait for IsStreaming to become false.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (composer.IsStreaming && DateTime.UtcNow < deadline)
                await Task.Delay(20, TestContext.Current.CancellationToken);

            Assert.False(composer.IsStreaming, "Streaming should have finished after the error");

            // Manually set _isStreaming to true to simulate an active stream.
            SetIsStreaming(composer, true);
            try
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => composer.CompactSessionAsync(TestContext.Current.CancellationToken));
                Assert.Contains("Cannot compact while streaming", ex.Message);
            }
            finally
            {
                // Cleanup: reset _isStreaming so the Composer doesn't hang.
                SetIsStreaming(composer, false);
            }
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    // ── 3. CompactSessionAsync_WithEnoughMessages_ReturnsTrueAndCompacts ──

    [Fact]
    public async Task CompactSessionAsync_WithEnoughMessages_ReturnsTrueAndCompacts()
    {
        var composer = CreateComposerWithMockSummaryClient(out var mockClient);
        InjectFakeChatClient(composer, mockClient.Object);

        var session = GetSession(composer);
        PopulateSession(session, 15); // 1 system + 15 user/assistant = 16 total, 15 non-system > 10+1
        var originalCount = session.MessageHistory.Count;
        Assert.Equal(16, originalCount);

        var result = await composer.CompactSessionAsync(TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.False(composer.IsCompacting);
        Assert.True(session.MessageHistory.Count < originalCount,
            $"Message count should have decreased after compaction (was {originalCount}, now {session.MessageHistory.Count})");
    }

    // ── 4. CompactSessionAsync_WithTooFewMessages_ReturnsFalse ──

    [Fact]
    public async Task CompactSessionAsync_WithTooFewMessages_ReturnsFalse()
    {
        var composer = CreateComposerWithMockSummaryClient(out var mockClient);
        InjectFakeChatClient(composer, mockClient.Object);

        var session = GetSession(composer);
        PopulateSession(session, 2); // 1 system + 2 user/assistant = 3 total, 2 non-system < 10+1

        var result = await composer.CompactSessionAsync(TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.False(composer.IsCompacting);
    }

    // ── 5. PostCompact_ReturnsOk_WhenEnoughMessages ──

    [Fact]
    public async Task PostCompact_ReturnsOk_WhenEnoughMessages()
    {
        await using var fixture = new ComposerHubWithConfigFixture(null);
        await fixture.InitializeAsync();

        // Access the fixture's private _composer field via reflection.
        var composerField = typeof(ComposerHubWithConfigFixture).GetField("_composer",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_composer field not found on fixture");
        var composer = (Composer)composerField.GetValue(fixture)!;

        // Replace the default mock IChatClient with one that returns a summary response.
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary")));
        InjectFakeChatClient(composer, mockClient.Object);

        // Add enough messages to trigger compaction.
        var session = GetSession(composer);
        PopulateSession(session, 15); // 16 total, 15 non-system > 10+1

        var response = await fixture.Client.PostAsync("/api/composer/compact", null, TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("compacted").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("messageCount").TryGetInt32(out _),
            "messageCount should be an integer");
    }

    // ── 6. PostCompact_ReturnsOkWithFalse_WhenTooFewMessages ──

    [Fact]
    public async Task PostCompact_ReturnsOkWithFalse_WhenTooFewMessages()
    {
        await using var fixture = new ComposerHubWithConfigFixture(null);
        await fixture.InitializeAsync();

        // Access the fixture's private _composer field via reflection.
        var composerField = typeof(ComposerHubWithConfigFixture).GetField("_composer",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_composer field not found on fixture");
        var composer = (Composer)composerField.GetValue(fixture)!;

        // Inject a mock chat client so the Composer has a valid agent.
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary")));
        InjectFakeChatClient(composer, mockClient.Object);

        // Do NOT add any messages — session starts empty (0 messages).

        var response = await fixture.Client.PostAsync("/api/composer/compact", null, TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("compacted").GetBoolean());
    }

    // ── 7. PostCompact_ReturnsBadRequest_WhenNotConnected ──

    [Fact]
    public async Task PostCompact_ReturnsBadRequest_WhenNotConnected()
    {
        var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);

        // Create a Composer that has NOT been connected (_agent is null).
        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            store,
            stateDir: tmpDir,
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:0");
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(composer);
        var app = builder.Build();
        app.MapComposerEndpoints(composer, null);
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

            var response = await client.PostAsync("/api/composer/compact", null, TestContext.Current.CancellationToken);

            Assert.False(response.IsSuccessStatusCode,
                "Should return a non-success status code when not connected");

            var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.TryGetProperty("error", out var errorProp),
                "Response should contain an 'error' property");
            Assert.Contains("not connected", errorProp.GetString()!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
            dbContext.Dispose();
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    // ── 8. CompactOldestPercentAsync_WhenNotConnected_ThrowsInvalidOperationException ──

    [Fact]
    public async Task CompactOldestPercentAsync_WhenNotConnected_ThrowsInvalidOperationException()
    {
        var composer = CreateComposerWithMockSummaryClient(out _);

        // Do NOT call ConnectAsync or InjectFakeChatClient — _agent is null.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => composer.CompactOldestPercentAsync(50, TestContext.Current.CancellationToken));

        Assert.Contains("not connected", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── 9. CompactOldestPercentAsync_WhileStreaming_ThrowsInvalidOperationException ──

    [Fact]
    public async Task CompactOldestPercentAsync_WhileStreaming_ThrowsInvalidOperationException()
    {
        var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            // Create a Composer whose AI client throws on both streaming and non-streaming paths.
            var overflowEx = new InvalidOperationException("model_max_prompt_tokens_exceeded");
            var mockClient = new Mock<IChatClient>();
            mockClient
                .Setup(c => c.GetStreamingResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .Throws(overflowEx);
            mockClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(overflowEx);

            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                store,
                stateDir: tmpDir);

            InjectFakeChatClient(composer, mockClient.Object);

            // Trigger streaming — it will fail quickly due to the throwing client.
            composer.SendMessage("test");

            // Wait for IsStreaming to become false.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (composer.IsStreaming && DateTime.UtcNow < deadline)
                await Task.Delay(20, TestContext.Current.CancellationToken);

            Assert.False(composer.IsStreaming, "Streaming should have finished after the error");

            // Manually set _isStreaming to true to simulate an active stream.
            SetIsStreaming(composer, true);
            try
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => composer.CompactOldestPercentAsync(50, TestContext.Current.CancellationToken));
                Assert.Contains("Cannot compact while streaming", ex.Message);
            }
            finally
            {
                // Cleanup: reset _isStreaming so the Composer doesn't hang.
                SetIsStreaming(composer, false);
            }
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    // ── 10. CompactOldestPercentAsync_WithEnoughMessages_ReturnsTrueAndCompacts ──

    [Fact]
    public async Task CompactOldestPercentAsync_WithEnoughMessages_ReturnsTrueAndCompacts()
    {
        var composer = CreateComposerWithMockSummaryClient(out var mockClient);
        InjectFakeChatClient(composer, mockClient.Object);

        var session = GetSession(composer);
        // 1 system + 30 user/assistant = 31 total. 50% of 30 non-system messages
        // must yield at least CompactionRetainRecent+1 (11) messages to compact.
        PopulateSession(session, 30);
        var originalCount = session.MessageHistory.Count;
        Assert.Equal(31, originalCount);

        var result = await composer.CompactOldestPercentAsync(50, TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.False(composer.IsCompacting);
        Assert.True(session.MessageHistory.Count < originalCount,
            $"Message count should have decreased after compaction (was {originalCount}, now {session.MessageHistory.Count})");
    }

    // ── 11. CompactOldestPercentAsync_WithTooFewMessages_ReturnsFalse ──

    [Fact]
    public async Task CompactOldestPercentAsync_WithTooFewMessages_ReturnsFalse()
    {
        var composer = CreateComposerWithMockSummaryClient(out var mockClient);
        InjectFakeChatClient(composer, mockClient.Object);

        var session = GetSession(composer);
        PopulateSession(session, 2); // 1 system + 2 user/assistant = 3 total

        var result = await composer.CompactOldestPercentAsync(50, TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.False(composer.IsCompacting);
    }

    // ── 12. PostCompactPartial_ReturnsOk_WhenEnoughMessages ──

    [Fact]
    public async Task PostCompactPartial_ReturnsOk_WhenEnoughMessages()
    {
        await using var fixture = new ComposerHubWithConfigFixture(null);
        await fixture.InitializeAsync();

        // Access the fixture's private _composer field via reflection.
        var composerField = typeof(ComposerHubWithConfigFixture).GetField("_composer",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_composer field not found on fixture");
        var composer = (Composer)composerField.GetValue(fixture)!;

        // Replace the default mock IChatClient with one that returns a summary response.
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary")));
        InjectFakeChatClient(composer, mockClient.Object);

        // Add enough messages to trigger compaction.
        var session = GetSession(composer);
        // 1 system + 30 user/assistant = 31 total. 50% of 30 non-system messages
        // must yield at least CompactionRetainRecent+1 (11) messages to compact.
        PopulateSession(session, 30);

        var response = await fixture.Client.PostAsync("/api/composer/compact-partial?percent=50", null, TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("compacted").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("messageCount").TryGetInt32(out _),
            "messageCount should be an integer");
    }

    // ── 13. PostCompactPartial_ReturnsOkWithFalse_WhenTooFewMessages ──

    [Fact]
    public async Task PostCompactPartial_ReturnsOkWithFalse_WhenTooFewMessages()
    {
        await using var fixture = new ComposerHubWithConfigFixture(null);
        await fixture.InitializeAsync();

        // Access the fixture's private _composer field via reflection.
        var composerField = typeof(ComposerHubWithConfigFixture).GetField("_composer",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_composer field not found on fixture");
        var composer = (Composer)composerField.GetValue(fixture)!;

        // Inject a mock chat client so the Composer has a valid agent.
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary")));
        InjectFakeChatClient(composer, mockClient.Object);

        // Do NOT add any messages — session starts empty (0 messages).

        var response = await fixture.Client.PostAsync("/api/composer/compact-partial?percent=50", null, TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("compacted").GetBoolean());
    }

    // ── 14. PostCompactPartial_ReturnsBadRequest_WhenNotConnected ──
    //
    // This test boots the real application via WebApplicationFactory<Program>,
    // so it lives in its own [Collection("HiveIntegration")] class below
    // (ComposerCompactPartialNotConnectedTests) to avoid parallel SQLite write
    // conflicts and to comply with the project convention of using
    // WebApplicationFactory<Program> for endpoint hosting.
}

/// <summary>
/// Integration test for the <c>POST /api/composer/compact-partial</c> endpoint when the
/// Composer has not been connected. Uses <see cref="WebApplicationFactory{Program}"/> via
/// <see cref="ComposerCompactPartialEndpointFactory"/> (per project convention) instead of
/// constructing a host with <c>WebApplication.CreateBuilder()</c>.
/// </summary>
[Collection("HiveIntegration")]
public sealed class ComposerCompactPartialNotConnectedTests
{
    [Fact]
    public async Task PostCompactPartial_ReturnsBadRequest_WhenNotConnected()
    {
        using var factory = new ComposerCompactPartialEndpointFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/composer/compact-partial?percent=50",
            null,
            TestContext.Current.CancellationToken);

        Assert.False(response.IsSuccessStatusCode,
            "Should return a non-success status code when not connected");

        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out var errorProp),
            "Response should contain an 'error' property");
        Assert.Contains("not connected", errorProp.GetString()!, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Custom <see cref="WebApplicationFactory{Program}"/> that replaces the real Composer
/// singleton with an unconnected instance (its <c>_agent</c> is null because
/// <c>ConnectAsync</c> is never invoked on it). This lets the
/// <c>/api/composer/compact-partial</c> endpoint exercise the "not connected" path.
/// </summary>
internal sealed class ComposerCompactPartialEndpointFactory : WebApplicationFactory<Program>
{
    private readonly string _tmpDir =
        Path.Combine(Path.GetTempPath(), $"copilothive-compactpartial-{Guid.NewGuid():N}");
    private readonly string _stateDir;
    private CopilotHiveDbContext? _dbContext;

    public ComposerCompactPartialEndpointFactory()
    {
        _stateDir = Path.Combine(_tmpDir, "state");
        Directory.CreateDirectory(_stateDir);
        Environment.SetEnvironmentVariable("STATE_DIR", _stateDir);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Replace the real Composer singleton with an unconnected instance.
            var existing = services.SingleOrDefault(d => d.ServiceType == typeof(Composer));
            if (existing is not null)
                services.Remove(existing);

            _dbContext = CopilotHiveDbContext.CreateInMemory();
            var store = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);

            // NOT connected: the chat-client factory throws, so Program.cs's
            // startup call to composer.ConnectAsync() (wrapped in try/catch) fails
            // gracefully and leaves _agent null — the "not connected" state under test.
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                store,
                stateDir: _tmpDir,
                chatClientFactory: _ => throw new InvalidOperationException(
                    "chat client unavailable in test — Composer stays unconnected"));

            services.AddSingleton(composer);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Environment.SetEnvironmentVariable("STATE_DIR", null);
        _dbContext?.Dispose();

        if (!disposing || !Directory.Exists(_tmpDir))
            return;

        try
        {
            Directory.Delete(_tmpDir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}