using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CopilotHive;
using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    /// <c>CodingAgent</c> by calling the private <c>RecreateAgentAsync()</c> method.
    /// Call this BEFORE <c>SendMessage</c> — no <c>ConnectAsync</c> call is needed.
    /// </summary>
    private static async Task InjectFakeChatClient(Composer composer, IChatClient fakeClient)
    {
        var agentService = GetAgentService(composer);
        var serviceType = agentService.GetType();

        var chatClientField = serviceType.GetField("_chatClient",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_chatClient field not found on ComposerAgentService");
        chatClientField.SetValue(agentService, fakeClient);

        var recreateAgent = serviceType.GetMethod("RecreateAgentAsync",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("RecreateAgentAsync method not found on ComposerAgentService");
        await (Task)recreateAgent.Invoke(agentService, null)!;
    }

    /// <summary>
    /// Gets the private <c>_session</c> field from a <see cref="Composer"/> instance.
    /// </summary>
    private static AgentSession GetSession(Composer composer)
    {
        var agentService = GetAgentService(composer);
        var sessionField = agentService.GetType().GetField("_session",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_session field not found on ComposerAgentService");
        return (AgentSession)sessionField.GetValue(agentService)!;
    }

    /// <summary>Gets the private <c>_agentService</c> instance from a <see cref="Composer"/>.</summary>
    private static object GetAgentService(Composer composer)
    {
        var field = typeof(Composer).GetField("_agentService",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_agentService field not found on Composer");
        return field.GetValue(composer)
            ?? throw new InvalidOperationException("_agentService was null");
    }

    /// <summary>Test seam: sets the facade's volatile _isStreaming flag via reflection.</summary>
    private static void SetFacadeStreaming(Composer composer, bool isStreaming)
    {
        var field = typeof(Composer).GetField(
            "_isStreaming",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_isStreaming field not found on Composer");
        field.SetValue(composer, isStreaming);
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

    /// <summary>
    /// Captures rather than runs a posted continuation. This lets a test force the ordering
    /// "q1 completes → synchronous completion continuation publishes q2 → AskUserAsync finally
    /// resumes" without sleeps or polling.
    /// </summary>
    private sealed class GatedSynchronizationContext : SynchronizationContext
    {
        private readonly TaskCompletionSource<(SendOrPostCallback Callback, object? State)> _posted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Posted => _posted.Task;

        public override void Post(SendOrPostCallback d, object? state) =>
            _posted.TrySetResult((d, state));

        internal async Task RunPostedAsync(CancellationToken cancellationToken)
        {
            var work = await _posted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                work.Callback(work.State);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }

    private static void SetFacadeSessionLoadedFromDisk(Composer composer, bool value)
    {
        var field = typeof(Composer).GetField(
            "_sessionLoadedFromDisk",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_sessionLoadedFromDisk not found on Composer");
        field.SetValue(composer, value);
    }

    /// <summary>
    /// Logger gate used to cancel a reconnect after disk load and agent recreation but before
    /// ComposerAgentService commits its loaded-from-disk flag. The gate is armed only after the
    /// initial successful connection.
    /// </summary>
    private sealed class ArmableGatedLogger<T>(string messageFragment) : ILogger<T>
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool Armed { get; set; }
        internal Task Entered => _entered.Task;
        internal void Release() => _release.TrySetResult();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!Armed || !formatter(state, exception).Contains(messageFragment, StringComparison.Ordinal))
                return;

            _entered.TrySetResult();
            _release.Task.GetAwaiter().GetResult();
        }
    }

    private static async Task<AgentSession> CompleteLateCancelledReconnectAsync(
        Composer composer,
        ComposerAgentService agentService,
        ArmableGatedLogger<Composer> logger)
    {
        logger.Armed = true;
        using var reconnectCts = new CancellationTokenSource();
        var reconnectTask = composer.ConnectAsync(reconnectCts.Token);

        await logger.Entered.WaitAsync(
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(composer.SessionLoadedFromDisk);
        Assert.False(reconnectTask.IsCompleted);

        reconnectCts.Cancel();
        logger.Release();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconnectTask);

        // The token-insensitive tail committed true in the service; cancellation authority
        // remains in the actor-owned facade cache.
        Assert.True(agentService.SessionLoadedFromDisk);
        Assert.False(composer.SessionLoadedFromDisk);
        return agentService.Session;
    }

    /// <summary>
    /// Logger that can be armed after the initial connection. It throws on the second agent's
    /// final creation log (after ResetSessionAsync installed a fresh session) and again on the
    /// overflow-failure LogError call, proving cache publication and terminal delivery survive
    /// both failures.
    /// </summary>
    private sealed class ArmableThrowingLogger<T>(string messageFragment, string failureMessage) : ILogger<T>
    {
        private int _overflowFailureLogAttempts;

        internal bool Armed { get; set; }
        internal int OverflowFailureLogAttempts => Volatile.Read(ref _overflowFailureLogAttempts);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!Armed)
                return;

            var message = formatter(state, exception);
            if (message.Contains("Composer overflow recovery failed", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _overflowFailureLogAttempts);
                throw new InvalidOperationException("overflow failure logger threw");
            }

            if (message.Contains(messageFragment, StringComparison.Ordinal))
                throw new InvalidOperationException(failureMessage);
        }
    }

    /// <summary>
    /// First streaming response calls the real ask_user tool. The second response is held behind
    /// a TCS gate, proving the stream resumed after the answer while remaining in flight.
    /// </summary>
    private sealed class AskUserThenBlockStreamingClient : IChatClient
    {
        private readonly TaskCompletionSource _firstRequestEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondRequestEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSecondResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;

        internal Task FirstRequestEntered => _firstRequestEntered.Task;
        internal Task SecondRequestEntered => _secondRequestEntered.Task;
        internal void ReleaseSecondResponse() => _releaseSecondResponse.TrySetResult();

        public ChatClientMetadata Metadata => new("ask-user-gated", null, "test-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This client is streaming-only.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var request = Interlocked.Increment(ref _requestCount);
            if (request == 1)
            {
                _firstRequestEntered.TrySetResult();
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent(
                        "ask-user-1",
                        "ask_user",
                        new Dictionary<string, object?>
                        {
                            ["question"] = "Continue the suspended stream?",
                            ["type"] = "YesNo",
                        })])
                {
                    FinishReason = ChatFinishReason.ToolCalls,
                };
                yield break;
            }

            if (request == 2)
            {
                _secondRequestEntered.TrySetResult();
                await _releaseSecondResponse.Task.WaitAsync(cancellationToken);
                yield return new ChatResponseUpdate(ChatRole.Assistant, "resumed")
                {
                    FinishReason = ChatFinishReason.Stop,
                };
                yield break;
            }

            throw new InvalidOperationException($"Unexpected streaming request {request}.");
        }

        public void Dispose() => ReleaseSecondResponse();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
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

            await InjectFakeChatClient(composer, mockClient.Object);

            // Trigger streaming — it will fail quickly due to the throwing client.
            composer.SendMessage("test");

            // Wait for IsStreaming to become false.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (composer.IsStreaming && DateTime.UtcNow < deadline)
                await Task.Delay(20, TestContext.Current.CancellationToken);

            Assert.False(composer.IsStreaming, "Streaming should have finished after the error");

            // Manually set _isStreaming to true to simulate an active stream.
            SetFacadeStreaming(composer, true);
            try
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => composer.CompactSessionAsync(TestContext.Current.CancellationToken));
                Assert.Contains("Cannot compact while streaming", ex.Message);
            }
            finally
            {
                // Cleanup: reset _isStreaming so the Composer doesn't hang.
                SetFacadeStreaming(composer, false);
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
        await InjectFakeChatClient(composer, mockClient.Object);

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
        await InjectFakeChatClient(composer, mockClient.Object);

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
        await InjectFakeChatClient(composer, mockClient.Object);

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
        await InjectFakeChatClient(composer, mockClient.Object);

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
        app.MapComposerEndpoints(
            composer,
            new ComposerFacade(composer, NullLogger<ComposerFacade>.Instance),
            null);
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

            await InjectFakeChatClient(composer, mockClient.Object);

            // Trigger streaming — it will fail quickly due to the throwing client.
            composer.SendMessage("test");

            // Wait for IsStreaming to become false.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (composer.IsStreaming && DateTime.UtcNow < deadline)
                await Task.Delay(20, TestContext.Current.CancellationToken);

            Assert.False(composer.IsStreaming, "Streaming should have finished after the error");

            // Manually set _isStreaming to true to simulate an active stream.
            SetFacadeStreaming(composer, true);
            try
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => composer.CompactOldestPercentAsync(50, TestContext.Current.CancellationToken));
                Assert.Contains("Cannot compact while streaming", ex.Message);
            }
            finally
            {
                // Cleanup: reset _isStreaming so the Composer doesn't hang.
                SetFacadeStreaming(composer, false);
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
        await InjectFakeChatClient(composer, mockClient.Object);

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
        await InjectFakeChatClient(composer, mockClient.Object);

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
        await InjectFakeChatClient(composer, mockClient.Object);

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
        await InjectFakeChatClient(composer, mockClient.Object);

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

    // ── 15. Compaction exactly-once callbacks (facade level) ──

    private static string GetComposerStateDir(Composer composer)
    {
        var field = typeof(Composer).GetField("_stateDir",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_stateDir field not found on Composer");
        return (string)field.GetValue(composer)!;
    }

    /// <summary>
    /// A successful full compaction fires <see cref="Composer.OnCompactingStarted"/> and
    /// <see cref="Composer.OnCompacted"/> exactly once each (event count == 1), and
    /// <see cref="Composer.WasCompacted"/> is true, <see cref="Composer.IsCompacting"/> false.
    /// The agent service's wired <c>OnCompacting</c>/<c>OnCompacted</c> callbacks are NOT
    /// invoked (callback-free options for manual compaction).
    /// </summary>
    [Fact]
    public async Task CompactSessionAsync_Success_EventsFireOnce_WasCompactedTrue_IsCompactingFalse()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var mockClient = new Mock<IChatClient>();
            mockClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary of conversation")));

            var dbContext = CopilotHiveDbContext.CreateInMemory();
            var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                store,
                stateDir: tmpDir,
                chatClientFactory: _ => mockClient.Object);

            var startedCount = 0;
            var compactedCount = 0;
            composer.OnCompactingStarted += () => Interlocked.Increment(ref startedCount);
            composer.OnCompacted += () => Interlocked.Increment(ref compactedCount);

            await InjectFakeChatClient(composer, mockClient.Object);
            var session = GetSession(composer);
            PopulateSession(session, 15);
            var originalCount = session.MessageHistory.Count;

            var result = await composer.CompactSessionAsync(TestContext.Current.CancellationToken);

            Assert.True(result);
            Assert.Equal(1, startedCount);
            Assert.Equal(1, compactedCount);
            Assert.True(composer.WasCompacted, "WasCompacted must be true on success");
            Assert.False(composer.IsCompacting, "IsCompacting must be false after success");
            Assert.True(session.MessageHistory.Count < originalCount);

            await composer.DisposeAsync();
            dbContext.Dispose();
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task CompactSessionAsync_Failure_EventsFireOnce_WasCompactedFalse_IsCompactingFalse()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var mockClient = new Mock<IChatClient>();
            mockClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("compaction backend boom"));

            var dbContext = CopilotHiveDbContext.CreateInMemory();
            var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                store,
                stateDir: tmpDir,
                chatClientFactory: _ => mockClient.Object);

            var startedCount = 0;
            var compactedCount = 0;
            composer.OnCompactingStarted += () => Interlocked.Increment(ref startedCount);
            composer.OnCompacted += () => Interlocked.Increment(ref compactedCount);

            await InjectFakeChatClient(composer, mockClient.Object);
            PopulateSession(GetSession(composer), 15);

            var result = await composer.CompactSessionAsync(TestContext.Current.CancellationToken);

            Assert.False(result);
            Assert.Equal(1, startedCount);
            Assert.Equal(1, compactedCount);
            Assert.False(composer.WasCompacted, "WasCompacted must be false on failure");
            Assert.False(composer.IsCompacting, "IsCompacting must be false after failure");

            await composer.DisposeAsync();
            dbContext.Dispose();
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task CompactSessionAsync_Cancel_EventsFireOnce_WasCompactedFalse_IsCompactingFalse()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var mockClient = new Mock<IChatClient>();
            mockClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException(new CancellationToken(true)));

            var dbContext = CopilotHiveDbContext.CreateInMemory();
            var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                store,
                stateDir: tmpDir,
                chatClientFactory: _ => mockClient.Object);

            var startedCount = 0;
            var compactedCount = 0;
            composer.OnCompactingStarted += () => Interlocked.Increment(ref startedCount);
            composer.OnCompacted += () => Interlocked.Increment(ref compactedCount);

            await InjectFakeChatClient(composer, mockClient.Object);
            PopulateSession(GetSession(composer), 15);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var result = await composer.CompactSessionAsync(cts.Token);

            Assert.False(result);
            Assert.Equal(1, startedCount);
            Assert.Equal(1, compactedCount);
            Assert.False(composer.WasCompacted, "WasCompacted must be false on cancel");
            Assert.False(composer.IsCompacting, "IsCompacting must be false after cancel");

            await composer.DisposeAsync();
            dbContext.Dispose();
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task CompactOldestPercentAsync_Success_EventsFireOnce_WasCompactedTrue()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var mockClient = new Mock<IChatClient>();
            mockClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary")));

            var dbContext = CopilotHiveDbContext.CreateInMemory();
            var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                store,
                stateDir: tmpDir,
                chatClientFactory: _ => mockClient.Object);

            var startedCount = 0;
            var compactedCount = 0;
            composer.OnCompactingStarted += () => Interlocked.Increment(ref startedCount);
            composer.OnCompacted += () => Interlocked.Increment(ref compactedCount);

            await InjectFakeChatClient(composer, mockClient.Object);
            var session = GetSession(composer);
            PopulateSession(session, 30);
            var originalCount = session.MessageHistory.Count;

            var result = await composer.CompactOldestPercentAsync(50, TestContext.Current.CancellationToken);

            Assert.True(result);
            Assert.Equal(1, startedCount);
            Assert.Equal(1, compactedCount);
            Assert.True(composer.WasCompacted, "WasCompacted must be true on partial success");
            Assert.False(composer.IsCompacting, "IsCompacting must be false after partial success");
            Assert.True(session.MessageHistory.Count < originalCount);

            await composer.DisposeAsync();
            dbContext.Dispose();
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task CompactOldestPercentAsync_Failure_EventsFireOnce_WasCompactedFalse()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var mockClient = new Mock<IChatClient>();
            mockClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("partial boom"));

            var dbContext = CopilotHiveDbContext.CreateInMemory();
            var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                store,
                stateDir: tmpDir,
                chatClientFactory: _ => mockClient.Object);

            var startedCount = 0;
            var compactedCount = 0;
            composer.OnCompactingStarted += () => Interlocked.Increment(ref startedCount);
            composer.OnCompacted += () => Interlocked.Increment(ref compactedCount);

            await InjectFakeChatClient(composer, mockClient.Object);
            PopulateSession(GetSession(composer), 30);

            var result = await composer.CompactOldestPercentAsync(50, TestContext.Current.CancellationToken);

            Assert.False(result);
            Assert.Equal(1, startedCount);
            Assert.Equal(1, compactedCount);
            Assert.False(composer.WasCompacted, "WasCompacted must be false on partial failure");
            Assert.False(composer.IsCompacting, "IsCompacting must be false after partial failure");

            await composer.DisposeAsync();
            dbContext.Dispose();
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    // ── 16. SessionLoadedFromDisk (facade level) ──

    /// <summary>
    /// <see cref="Composer.SessionLoadedFromDisk"/> is false before connect, true on a disk
    /// load. No stale true.
    /// </summary>
    [Fact]
    public async Task SessionLoadedFromDisk_FalseBeforeConnect_TrueOnDiskLoad()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var mockClient = new Mock<IChatClient>();
            mockClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hi")));

            var dbContext = CopilotHiveDbContext.CreateInMemory();
            var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                store,
                stateDir: tmpDir,
                chatClientFactory: _ => mockClient.Object);

            // Before connect, the cache is false.
            Assert.False(composer.SessionLoadedFromDisk);

            // Write a valid session file so ConnectAsync loads it from disk.
            var stateDir = GetComposerStateDir(composer);
            var sessionFile = Path.Combine(stateDir, "composer-session.json");
            var validSession = AgentSession.Create("composer");
            validSession.MessageHistory.Add(new ChatMessage(ChatRole.User, "persisted"));
            await validSession.SaveAsync(sessionFile, TestContext.Current.CancellationToken);

            await composer.ConnectAsync(TestContext.Current.CancellationToken);

            Assert.True(composer.SessionLoadedFromDisk, "SessionLoadedFromDisk must be true on a disk-loaded connect");

            await composer.DisposeAsync();
            dbContext.Dispose();
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    /// <summary>
    /// <see cref="Composer.SessionLoadedFromDisk"/> is false on a fresh connect (no disk file).
    /// </summary>
    [Fact]
    public async Task SessionLoadedFromDisk_FalseOnFreshConnect_NoDiskFile()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var mockClient = new Mock<IChatClient>();
            mockClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hi")));

            var dbContext = CopilotHiveDbContext.CreateInMemory();
            var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                store,
                stateDir: tmpDir,
                chatClientFactory: _ => mockClient.Object);

            await composer.ConnectAsync(TestContext.Current.CancellationToken);

            Assert.False(composer.SessionLoadedFromDisk, "SessionLoadedFromDisk must be false on a fresh (no-disk) connect");

            await composer.DisposeAsync();
            dbContext.Dispose();
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    /// <summary>
    /// <see cref="Composer.SessionLoadedFromDisk"/> is false on a connect failure — no stale true
    /// even when a session file exists (the connection failed after loading it).
    /// </summary>
    [Fact]
    public async Task SessionLoadedFromDisk_FalseOnConnectFailure_NoStaleTrue()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var dbContext = CopilotHiveDbContext.CreateInMemory();
            var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                store,
                stateDir: tmpDir,
                chatClientFactory: _ => throw new InvalidOperationException("client creation boom"));

            // Write a valid session file so the load step runs before the failure.
            var stateDir = GetComposerStateDir(composer);
            var sessionFile = Path.Combine(stateDir, "composer-session.json");
            var validSession = AgentSession.Create("composer");
            await validSession.SaveAsync(sessionFile, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidOperationException>(() => composer.ConnectAsync(TestContext.Current.CancellationToken));

            Assert.False(composer.SessionLoadedFromDisk, "SessionLoadedFromDisk must be false on failure — no stale true");

            await composer.DisposeAsync();
            dbContext.Dispose();
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    /// <summary>
    /// <see cref="Composer.SessionLoadedFromDisk"/> is false on a cancelled connect — no stale true.
    /// </summary>
    [Fact]
    public async Task SessionLoadedFromDisk_FalseOnConnectCancel_NoStaleTrue()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var mockClient = new Mock<IChatClient>();
            mockClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hi")));

            var dbContext = CopilotHiveDbContext.CreateInMemory();
            var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                store,
                stateDir: tmpDir,
                chatClientFactory: _ => mockClient.Object);

            // Write a valid session file so the session-load step honors the cancelled token.
            var stateDir = GetComposerStateDir(composer);
            var sessionFile = Path.Combine(stateDir, "composer-session.json");
            var validSession = AgentSession.Create("composer");
            await validSession.SaveAsync(sessionFile, TestContext.Current.CancellationToken);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => composer.ConnectAsync(cts.Token));

            Assert.False(composer.SessionLoadedFromDisk, "SessionLoadedFromDisk must be false on cancel — no stale true");

            await composer.DisposeAsync();
            dbContext.Dispose();
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task SessionLoadedFromDisk_PreCancelledReconnect_ClearsBeforeReplyAndAgainOnCancelledPath()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            store,
            stateDir: tmpDir,
            chatClientFactory: _ => new Mock<IChatClient>().Object);
        var agentService = Assert.IsType<ComposerAgentService>(GetAgentService(composer));
        var disposalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var sessionFile = Path.Combine(tmpDir, "composer-session.json");
            var persisted = AgentSession.Create("composer");
            persisted.MessageHistory.Add(new ChatMessage(ChatRole.User, "loaded reconnect session"));
            await persisted.SaveAsync(sessionFile, TestContext.Current.CancellationToken);

            await composer.ConnectAsync(TestContext.Current.CancellationToken);
            Assert.True(composer.SessionLoadedFromDisk);

            // The second ConnectAsync reaches this hook only after the actor has synchronously
            // published onSessionLoaded(false), but before the service can settle the reply.
            agentService.OnAgentDisposing = _ =>
            {
                disposalEntered.TrySetResult();
                releaseDisposal.Task.GetAwaiter().GetResult();
            };
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var reconnectTask = composer.ConnectAsync(cts.Token);
            await disposalEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.False(composer.SessionLoadedFromDisk,
                "The actor must clear the facade cache before the reconnect reply can settle");
            Assert.False(reconnectTask.IsCompleted,
                "The disposal gate must hold the actor before the reconnect reply settles");

            // Reintroduce a stale true after the initial clear. Only the actor's cancelled catch
            // can clear this value before completing the cancelled reply; removing that callback
            // therefore fails the final assertion.
            SetFacadeSessionLoadedFromDisk(composer, true);
            releaseDisposal.TrySetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconnectTask);
            Assert.False(composer.SessionLoadedFromDisk);
        }
        finally
        {
            releaseDisposal.TrySetResult();
            agentService.OnAgentDisposing = null;
            await composer.DisposeAsync();
            dbContext.Dispose();
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task SessionLoadedFromDisk_FailedReconnect_ClearsStaleTrueOnExceptionPath()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            store,
            stateDir: tmpDir,
            chatClientFactory: _ => new Mock<IChatClient>().Object);
        var agentService = Assert.IsType<ComposerAgentService>(GetAgentService(composer));
        var disposalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var sessionFile = Path.Combine(tmpDir, "composer-session.json");
            var persisted = AgentSession.Create("composer");
            persisted.MessageHistory.Add(new ChatMessage(ChatRole.User, "loaded reconnect session"));
            await persisted.SaveAsync(sessionFile, TestContext.Current.CancellationToken);

            await composer.ConnectAsync(TestContext.Current.CancellationToken);
            Assert.True(composer.SessionLoadedFromDisk);

            agentService.OnAgentDisposing = _ =>
            {
                disposalEntered.TrySetResult();
                releaseDisposal.Task.GetAwaiter().GetResult();
                throw new InvalidOperationException("reconnect disposal failed");
            };

            var reconnectTask = composer.ConnectAsync(TestContext.Current.CancellationToken);
            await disposalEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(composer.SessionLoadedFromDisk);
            Assert.False(reconnectTask.IsCompleted);

            // Force a stale publish while ConnectAsync is gated. The exception-path callback is
            // now the only production operation that can clear it before the faulted reply.
            SetFacadeSessionLoadedFromDisk(composer, true);
            releaseDisposal.TrySetResult();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => reconnectTask);
            Assert.Equal("reconnect disposal failed", ex.Message);
            Assert.False(composer.SessionLoadedFromDisk);
        }
        finally
        {
            releaseDisposal.TrySetResult();
            agentService.OnAgentDisposing = null;
            await composer.DisposeAsync();
            dbContext.Dispose();
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task SessionLoadedFromDisk_PreCancelledResetWithoutReplacement_PreservesTrue()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            store,
            stateDir: tmpDir,
            chatClientFactory: _ => new Mock<IChatClient>().Object);
        var agentService = Assert.IsType<ComposerAgentService>(GetAgentService(composer));

        try
        {
            var sessionFile = Path.Combine(tmpDir, "composer-session.json");
            var persisted = AgentSession.Create("composer");
            persisted.MessageHistory.Add(new ChatMessage(ChatRole.User, "pre-cancel session stays"));
            await persisted.SaveAsync(sessionFile, TestContext.Current.CancellationToken);

            await composer.ConnectAsync(TestContext.Current.CancellationToken);
            Assert.True(composer.SessionLoadedFromDisk);
            var sessionBefore = agentService.Session;

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => composer.ResetSessionAsync(cts.Token));

            Assert.Same(sessionBefore, agentService.Session);
            Assert.True(agentService.SessionLoadedFromDisk);
            Assert.True(composer.SessionLoadedFromDisk);
            Assert.Contains(
                agentService.Session.MessageHistory,
                message => message.Text == "pre-cancel session stays");
        }
        finally
        {
            await composer.DisposeAsync();
            dbContext.Dispose();
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task SessionLoadedFromDisk_LateCancelledReconnectThenPreCancelledReset_NeverResurrectsTrue()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
        var logger = new ArmableGatedLogger<Composer>("Composer CodingAgent created");
        var composer = new Composer(
            "test-model",
            logger,
            store,
            stateDir: tmpDir,
            chatClientFactory: _ => new Mock<IChatClient>().Object);
        var agentService = Assert.IsType<ComposerAgentService>(GetAgentService(composer));

        try
        {
            var sessionFile = Path.Combine(tmpDir, "composer-session.json");
            var persisted = AgentSession.Create("composer");
            persisted.MessageHistory.Add(new ChatMessage(ChatRole.User, "late-cancel persisted session"));
            await persisted.SaveAsync(sessionFile, TestContext.Current.CancellationToken);

            await composer.ConnectAsync(TestContext.Current.CancellationToken);
            Assert.True(composer.SessionLoadedFromDisk);

            // Cancel at the gated RecreateAgentAsync tail, after disk load but before the
            // service commits its flag.
            var sessionAfterLateCancel = await CompleteLateCancelledReconnectAsync(
                composer, agentService, logger);
            Assert.Contains(
                sessionAfterLateCancel.MessageHistory,
                message => message.Text == "late-cancel persisted session");

            // A pre-cancelled reset throws before mutating the service. The identity-based
            // cancellation handler must preserve the authoritative facade false, not resurrect
            // the stale service-level true.
            using var resetCts = new CancellationTokenSource();
            resetCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => composer.ResetSessionAsync(resetCts.Token));

            Assert.Same(sessionAfterLateCancel, agentService.Session);
            Assert.True(agentService.SessionLoadedFromDisk);
            Assert.False(composer.SessionLoadedFromDisk);
        }
        finally
        {
            logger.Release();
            await composer.DisposeAsync();
            dbContext.Dispose();
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task SessionLoadedFromDisk_LateCancelledConnectThenPreReplacementResetFailure_PreservesFalse()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
        var logger = new ArmableGatedLogger<Composer>("Composer CodingAgent created");
        var composer = new Composer(
            "test-model",
            logger,
            store,
            stateDir: tmpDir,
            chatClientFactory: _ => new Mock<IChatClient>().Object);
        var agentService = Assert.IsType<ComposerAgentService>(GetAgentService(composer));
        const string resetFailure = "reset disposal failed before replacement";

        try
        {
            var sessionFile = Path.Combine(tmpDir, "composer-session.json");
            var persisted = AgentSession.Create("composer");
            persisted.MessageHistory.Add(new ChatMessage(ChatRole.User, "stale-service session"));
            await persisted.SaveAsync(sessionFile, TestContext.Current.CancellationToken);

            await composer.ConnectAsync(TestContext.Current.CancellationToken);
            Assert.True(composer.SessionLoadedFromDisk);
            var sessionAfterLateCancel = await CompleteLateCancelledReconnectAsync(
                composer, agentService, logger);

            agentService.OnAgentDisposing = _ => throw new InvalidOperationException(resetFailure);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => composer.ResetSessionAsync(TestContext.Current.CancellationToken));

            Assert.Equal(resetFailure, ex.Message);
            Assert.Same(sessionAfterLateCancel, agentService.Session);
            Assert.True(agentService.SessionLoadedFromDisk);
            Assert.False(composer.SessionLoadedFromDisk);
        }
        finally
        {
            logger.Release();
            agentService.OnAgentDisposing = null;
            await composer.DisposeAsync();
            dbContext.Dispose();
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task SessionLoadedFromDisk_AfterResetSessionAsync_IsFalse()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
        var client = new Mock<IChatClient>();
        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            store,
            stateDir: tmpDir,
            chatClientFactory: _ => client.Object);

        try
        {
            var sessionFile = Path.Combine(tmpDir, "composer-session.json");
            var persisted = AgentSession.Create("composer");
            persisted.MessageHistory.Add(new ChatMessage(ChatRole.User, "loaded from disk"));
            await persisted.SaveAsync(sessionFile, TestContext.Current.CancellationToken);

            await composer.ConnectAsync(TestContext.Current.CancellationToken);
            Assert.True(composer.SessionLoadedFromDisk);

            // The actor clears the facade cache in actor order before this reply completes.
            await composer.ResetSessionAsync(TestContext.Current.CancellationToken);

            Assert.False(composer.SessionLoadedFromDisk);
            Assert.Empty(GetSession(composer).MessageHistory);
            Assert.False(File.Exists(sessionFile));
        }
        finally
        {
            await composer.DisposeAsync();
            dbContext.Dispose();
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task SessionLoadedFromDisk_ResetCancelledAfterReplacement_PublishesFalse()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
        var logger = new ArmableGatedLogger<Composer>("Composer CodingAgent created");
        var composer = new Composer(
            "test-model",
            logger,
            store,
            stateDir: tmpDir,
            chatClientFactory: _ => new Mock<IChatClient>().Object);
        var agentService = Assert.IsType<ComposerAgentService>(GetAgentService(composer));

        try
        {
            var sessionFile = Path.Combine(tmpDir, "composer-session.json");
            var persisted = AgentSession.Create("composer");
            persisted.MessageHistory.Add(new ChatMessage(ChatRole.User, "replace on reset"));
            await persisted.SaveAsync(sessionFile, TestContext.Current.CancellationToken);

            await composer.ConnectAsync(TestContext.Current.CancellationToken);
            Assert.True(composer.SessionLoadedFromDisk);
            var sessionBefore = agentService.Session;

            logger.Armed = true;
            using var cts = new CancellationTokenSource();
            var resetTask = composer.ResetSessionAsync(cts.Token);

            // RecreateAgentAsync's final log is after ResetSessionAsync assigned the fresh
            // session. Cancel while that token-insensitive stage is gated.
            await logger.Entered.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.NotSame(sessionBefore, agentService.Session);
            Assert.False(agentService.SessionLoadedFromDisk);
            Assert.False(resetTask.IsCompleted);
            cts.Cancel();
            logger.Release();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resetTask);
            Assert.NotSame(sessionBefore, agentService.Session);
            Assert.False(agentService.SessionLoadedFromDisk);
            Assert.False(composer.SessionLoadedFromDisk);
            Assert.Empty(agentService.Session.MessageHistory);
        }
        finally
        {
            logger.Release();
            await composer.DisposeAsync();
            dbContext.Dispose();
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task SessionLoadedFromDisk_ResetFailsAfterReplacement_PublishesFalse()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
        const string resetFailure = "direct reset recreation failed after replacement";
        var logger = new ArmableThrowingLogger<Composer>(
            "Composer CodingAgent created",
            resetFailure);
        var composer = new Composer(
            "test-model",
            logger,
            store,
            stateDir: tmpDir,
            chatClientFactory: _ => new Mock<IChatClient>().Object);
        var agentService = Assert.IsType<ComposerAgentService>(GetAgentService(composer));

        try
        {
            var sessionFile = Path.Combine(tmpDir, "composer-session.json");
            var persisted = AgentSession.Create("composer");
            persisted.MessageHistory.Add(new ChatMessage(ChatRole.User, "replace before failure"));
            await persisted.SaveAsync(sessionFile, TestContext.Current.CancellationToken);

            await composer.ConnectAsync(TestContext.Current.CancellationToken);
            Assert.True(composer.SessionLoadedFromDisk);
            var sessionBefore = agentService.Session;
            logger.Armed = true;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => composer.ResetSessionAsync(TestContext.Current.CancellationToken));

            Assert.Equal(resetFailure, ex.Message);
            Assert.NotSame(sessionBefore, agentService.Session);
            Assert.False(agentService.SessionLoadedFromDisk);
            Assert.False(composer.SessionLoadedFromDisk);
            Assert.Empty(agentService.Session.MessageHistory);
            Assert.Equal(0, logger.OverflowFailureLogAttempts);
        }
        finally
        {
            await composer.DisposeAsync();
            dbContext.Dispose();
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task SessionLoadedFromDisk_OverflowResetFailsBeforeReplacement_RemainsTrueAtErrorTerminal()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
        const string resetFailure = "reset disposal failed before session replacement";
        var overflow = new InvalidOperationException("model_max_prompt_tokens_exceeded");
        var client = new Mock<IChatClient>();
        client
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Throws(overflow);

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            store,
            stateDir: tmpDir,
            chatClientFactory: _ => client.Object);
        var agentService = Assert.IsType<ComposerAgentService>(GetAgentService(composer));
        var loadedAtError = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var streamFinished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var sessionFile = Path.Combine(tmpDir, "composer-session.json");
            var persisted = AgentSession.Create("composer");
            persisted.MessageHistory.Add(new ChatMessage(ChatRole.User, "old disk session remains"));
            await persisted.SaveAsync(sessionFile, TestContext.Current.CancellationToken);

            await composer.ConnectAsync(TestContext.Current.CancellationToken);
            Assert.True(composer.SessionLoadedFromDisk);
            var originalSession = agentService.Session;

            // ResetSessionAsync disposes the old agent before assigning a fresh session. Throw
            // from that exact seam so both service and facade keep describing the old session.
            agentService.OnAgentDisposing = _ => throw new InvalidOperationException(resetFailure);
            composer.OnStreamingUpdate += () =>
            {
                if (composer.StreamingContent.Contains(resetFailure, StringComparison.Ordinal))
                {
                    loadedAtError.TrySetResult(composer.SessionLoadedFromDisk);
                    if (!composer.IsStreaming)
                        streamFinished.TrySetResult();
                }
            };

            composer.SendMessage("overflow before reset replacement");

            Assert.True(await loadedAtError.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            await streamFinished.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.True(composer.SessionLoadedFromDisk);
            Assert.True(agentService.SessionLoadedFromDisk);
            Assert.Same(originalSession, agentService.Session);
            Assert.Contains(
                agentService.Session.MessageHistory,
                message => message.Text == "old disk session remains");
            Assert.Contains(resetFailure, composer.StreamingContent, StringComparison.Ordinal);
        }
        finally
        {
            agentService.OnAgentDisposing = null;
            await composer.DisposeAsync();
            dbContext.Dispose();
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task SessionLoadedFromDisk_LateCancelledConnectThenOverflowPreReplacementFailure_PreservesFalse()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
        var logger = new ArmableGatedLogger<Composer>("Composer CodingAgent created");
        const string resetFailure = "overflow reset disposal failed before replacement";
        var overflow = new InvalidOperationException("model_max_prompt_tokens_exceeded");
        var client = new Mock<IChatClient>();
        client
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Throws(overflow);
        var composer = new Composer(
            "test-model",
            logger,
            store,
            stateDir: tmpDir,
            chatClientFactory: _ => client.Object);
        var agentService = Assert.IsType<ComposerAgentService>(GetAgentService(composer));
        var loadedAtError = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var streamFinished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var sessionFile = Path.Combine(tmpDir, "composer-session.json");
            var persisted = AgentSession.Create("composer");
            persisted.MessageHistory.Add(new ChatMessage(ChatRole.User, "late-cancel overflow session"));
            await persisted.SaveAsync(sessionFile, TestContext.Current.CancellationToken);

            await composer.ConnectAsync(TestContext.Current.CancellationToken);
            Assert.True(composer.SessionLoadedFromDisk);
            var sessionAfterLateCancel = await CompleteLateCancelledReconnectAsync(
                composer, agentService, logger);

            agentService.OnAgentDisposing = _ => throw new InvalidOperationException(resetFailure);
            composer.OnStreamingUpdate += () =>
            {
                if (composer.StreamingContent.Contains(resetFailure, StringComparison.Ordinal))
                {
                    loadedAtError.TrySetResult(composer.SessionLoadedFromDisk);
                    if (!composer.IsStreaming)
                        streamFinished.TrySetResult();
                }
            };

            composer.SendMessage("overflow with stale service authority");

            Assert.False(await loadedAtError.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            await streamFinished.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.Same(sessionAfterLateCancel, agentService.Session);
            Assert.True(agentService.SessionLoadedFromDisk);
            Assert.False(composer.SessionLoadedFromDisk);
            Assert.Contains(resetFailure, composer.StreamingContent, StringComparison.Ordinal);
        }
        finally
        {
            logger.Release();
            agentService.OnAgentDisposing = null;
            await composer.DisposeAsync();
            dbContext.Dispose();
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task SessionLoadedFromDisk_OverflowSuccess_IsFalseWhenPublicCompletionCallbackFires()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
        var overflow = new InvalidOperationException("model_max_prompt_tokens_exceeded");
        var client = new Mock<IChatClient>();
        client
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Throws(overflow);

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            store,
            stateDir: tmpDir,
            chatClientFactory: _ => client.Object);
        var loadedAtCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var sessionFile = Path.Combine(tmpDir, "composer-session.json");
            var persisted = AgentSession.Create("composer");
            persisted.MessageHistory.Add(new ChatMessage(ChatRole.User, "overflowing disk session"));
            await persisted.SaveAsync(sessionFile, TestContext.Current.CancellationToken);

            await composer.ConnectAsync(TestContext.Current.CancellationToken);
            Assert.True(composer.SessionLoadedFromDisk);

            // SendMessage raises one admission update while IsStreaming is true. The first
            // update with IsStreaming=false is the public onStreamingFinished signal itself;
            // capture the cache value inside that callback, not after the handler returns.
            composer.OnStreamingUpdate += () =>
            {
                if (!composer.IsStreaming)
                    loadedAtCompletion.TrySetResult(composer.SessionLoadedFromDisk);
            };

            composer.SendMessage("overflow now");

            Assert.False(await loadedAtCompletion.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.False(composer.SessionLoadedFromDisk);
            Assert.False(composer.IsStreaming);
            Assert.Empty(GetSession(composer).MessageHistory);
            Assert.False(File.Exists(sessionFile));
        }
        finally
        {
            await composer.DisposeAsync();
            dbContext.Dispose();
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task SessionLoadedFromDisk_OverflowResetFailsAfterReplacement_ThrowingErrorLoggerStillPublishesFalse()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
        const string resetFailure = "reset failed after session replacement";
        var logger = new ArmableThrowingLogger<Composer>(
            "Composer CodingAgent created",
            resetFailure);
        var overflow = new InvalidOperationException("model_max_prompt_tokens_exceeded");
        var client = new Mock<IChatClient>();
        client
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Throws(overflow);

        var composer = new Composer(
            "test-model",
            logger,
            store,
            stateDir: tmpDir,
            chatClientFactory: _ => client.Object);
        var loadedAtError = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var streamFinished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var sessionFile = Path.Combine(tmpDir, "composer-session.json");
            var persisted = AgentSession.Create("composer");
            persisted.MessageHistory.Add(new ChatMessage(ChatRole.User, "overflowing disk session"));
            await persisted.SaveAsync(sessionFile, TestContext.Current.CancellationToken);

            await composer.ConnectAsync(TestContext.Current.CancellationToken);
            Assert.True(composer.SessionLoadedFromDisk);
            logger.Armed = true;

            composer.OnStreamingUpdate += () =>
            {
                if (composer.StreamingContent.Contains(resetFailure, StringComparison.Ordinal))
                {
                    // The first matching event is onStreamingError, i.e. the public error
                    // terminal. The actor must clear the cache before posting that terminal.
                    loadedAtError.TrySetResult(composer.SessionLoadedFromDisk);
                    if (!composer.IsStreaming)
                        streamFinished.TrySetResult();
                }
            };

            composer.SendMessage("overflow and fail reset");

            Assert.False(await loadedAtError.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            await streamFinished.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.False(composer.SessionLoadedFromDisk);
            Assert.False(Assert.IsType<ComposerAgentService>(GetAgentService(composer)).SessionLoadedFromDisk);
            Assert.False(composer.IsStreaming);
            Assert.Empty(GetSession(composer).MessageHistory);
            Assert.Equal(1, logger.OverflowFailureLogAttempts);
            Assert.Contains(resetFailure, composer.StreamingContent, StringComparison.Ordinal);
        }
        finally
        {
            await composer.DisposeAsync();
            dbContext.Dispose();
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    // ── 17. PendingQuestion lock protocol (facade level) ──

    /// <summary>
    /// <see cref="Composer.SubmitAnswer(string)"/> routes through the actor and completes the
    /// pending question; the PendingQuestion is cleared after delivery.
    /// </summary>
    [Fact]
    public async Task SubmitAnswer_RoutesThroughActor_CompletesPendingQuestion()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var mockClient = new Mock<IChatClient>();
            mockClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hi")));

            var dbContext = CopilotHiveDbContext.CreateInMemory();
            var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                store,
                stateDir: tmpDir,
                chatClientFactory: _ => mockClient.Object);

            await InjectFakeChatClient(composer, mockClient.Object);

            var q = new ComposerQuestion { Text = "Proceed?", Type = QuestionType.YesNo, Options = ["Yes", "No"] };
            var pendingProp = typeof(Composer).GetProperty("PendingQuestion",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(pendingProp);
            pendingProp!.SetValue(composer, q);

            Assert.NotNull(composer.PendingQuestion);
            Assert.Same(q, composer.PendingQuestion);

            composer.SubmitAnswer("Yes");

            var answer = await q.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal("Yes", answer);

            Assert.Null(composer.PendingQuestion);

            await composer.DisposeAsync();
            dbContext.Dispose();
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    /// <summary>
    /// <see cref="Composer.CancelQuestion"/> routes through the actor and completes the pending
    /// question with the exact "User cancelled the question without answering." message.
    /// </summary>
    [Fact]
    public async Task CancelQuestion_RoutesThroughActor_PreservesExactCancellationMessage()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var mockClient = new Mock<IChatClient>();
            mockClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hi")));

            var dbContext = CopilotHiveDbContext.CreateInMemory();
            var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                store,
                stateDir: tmpDir,
                chatClientFactory: _ => mockClient.Object);

            await InjectFakeChatClient(composer, mockClient.Object);

            var q = new ComposerQuestion { Text = "Proceed?", Type = QuestionType.YesNo, Options = ["Yes", "No"] };
            var pendingProp = typeof(Composer).GetProperty("PendingQuestion",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(pendingProp);
            pendingProp!.SetValue(composer, q);

            Assert.NotNull(composer.PendingQuestion);

            composer.CancelQuestion();

            var result = await q.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal("User cancelled the question without answering.", result);

            Assert.Null(composer.PendingQuestion);

            await composer.DisposeAsync();
            dbContext.Dispose();
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    /// <summary>
    /// Removal-proof suspended-stream test. The first real LLM response invokes ask_user and
    /// cannot issue its second request until the actor delivers the answer. The second request
    /// is then held behind a gate so the test proves resumption happened while streaming was
    /// still active, rather than merely observing an eventual answer after completion.
    /// </summary>
    [Fact]
    public async Task SubmitAnswer_WhileRealAskUserToolSuspendsStream_CompletesQuestionAndResumesBeforeStreamEnds()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
        var client = new AskUserThenBlockStreamingClient();
        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            store,
            stateDir: tmpDir,
            chatClientFactory: _ => client);
        var questionAsked = new TaskCompletionSource<ComposerQuestion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var streamFinished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        composer.OnQuestionAsked += () =>
        {
            if (composer.PendingQuestion is { } question)
                questionAsked.TrySetResult(question);
        };
        composer.OnStreamingUpdate += () =>
        {
            if (!composer.IsStreaming && client.SecondRequestEntered.IsCompleted)
                streamFinished.TrySetResult();
        };

        try
        {
            await composer.ConnectAsync(TestContext.Current.CancellationToken);
            composer.SendMessage("invoke ask_user");

            await client.FirstRequestEntered.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var question = await questionAsked.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.True(composer.IsStreaming);
            Assert.Same(question, composer.PendingQuestion);
            Assert.Equal("Continue the suspended stream?", question.Text);
            Assert.False(question.Completion.Task.IsCompleted);
            Assert.False(client.SecondRequestEntered.IsCompleted,
                "The LLM must still be suspended in ask_user before the answer is submitted");

            // Public API → actor mailbox while RunStreamingAsync is suspended in the tool.
            composer.SubmitAnswer("Yes, continue");

            Assert.Equal(
                "Yes, continue",
                await question.Completion.Task.WaitAsync(
                    TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            await client.SecondRequestEntered.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // The second LLM request proves ask_user returned and the stream resumed. Holding
            // that request proves delivery happened during the active stream, not afterward.
            Assert.True(composer.IsStreaming);
            Assert.Null(composer.PendingQuestion);
            Assert.False(streamFinished.Task.IsCompleted);

            client.ReleaseSecondResponse();
            await streamFinished.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(composer.IsStreaming);
            Assert.Contains("ask_user", composer.StreamingContent, StringComparison.Ordinal);
            Assert.EndsWith("resumed", composer.StreamingContent, StringComparison.Ordinal);
        }
        finally
        {
            client.ReleaseSecondResponse();
            composer.CancelStreaming();
            await composer.DisposeAsync();
            dbContext.Dispose();
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    /// <summary>
    /// Removal-proof race test for both answer routes. The real AskUserAsync publishes q1 and
    /// captures a gated synchronization context for its finally. A synchronous continuation on
    /// q1 publishes q2 through a second real AskUserAsync before q1's finally is released.
    /// Therefore an unconditional production clear erases q2 and fails this test. The same
    /// continuation observes Monitor.IsEntered to prove TrySetResult executes outside the
    /// production pending-question lock.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AskUserAsync_CompletionPublishesNewQuestion_ConditionalClearPreservesItAndCompletionRunsOutsideLock(
        bool cancelFirstQuestion)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        var dbContext = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            store,
            stateDir: tmpDir);

        var pendingLockField = typeof(Composer).GetField(
            "_pendingQuestionLock",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_pendingQuestionLock not found on Composer");
        var pendingLock = pendingLockField.GetValue(composer)
            ?? throw new InvalidOperationException("_pendingQuestionLock was null");

        var context = new GatedSynchronizationContext();
        Task<string> firstAskTask;
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            // Runs synchronously through PendingQuestion=q1, then suspends on q1.Completion.
            firstAskTask = composer.AskUserAsync(
                "First question?",
                type: "YesNo",
                cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        var q1 = Assert.IsType<ComposerQuestion>(composer.PendingQuestion);
        Assert.Equal("First question?", q1.Text);
        Assert.False(q1.Completion.Task.IsCompleted);

        var q2Published = new TaskCompletionSource<(ComposerQuestion Question, Task<string> AskTask)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completionRanWhileLockHeld = false;

        // ExecuteSynchronously is essential: this continuation runs inside the production
        // TrySetResult call. Monitor.IsEntered detects a regression that moves TrySetResult
        // under the lock (Monitor is re-entrant, so TryEnter would be vacuous here).
        var publishContinuation = q1.Completion.Task.ContinueWith(
            _ =>
            {
                try
                {
                    completionRanWhileLockHeld = Monitor.IsEntered(pendingLock);
                    var secondAskTask = composer.AskUserAsync(
                        "Second question?",
                        type: "YesNo",
                        cancellationToken: TestContext.Current.CancellationToken);
                    var q2 = Assert.IsType<ComposerQuestion>(composer.PendingQuestion);
                    q2Published.TrySetResult((q2, secondAskTask));
                }
                catch (Exception ex)
                {
                    q2Published.TrySetException(ex);
                    throw;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            // Public API → actor mailbox → real SubmitAnswerInternal/CancelQuestionInternal.
            if (cancelFirstQuestion)
                composer.CancelQuestion();
            else
                composer.SubmitAnswer("answer-one");

            var (q2, secondAskTask) = await q2Published.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // q1's AskUserAsync continuation is posted but intentionally not run yet. q2 was
            // published synchronously inside q1.Completion.TrySetResult.
            await context.Posted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Same(q2, composer.PendingQuestion);
            Assert.False(completionRanWhileLockHeld,
                "q1 completion ran while _pendingQuestionLock was held; TrySetResult must be outside the lock");

            // Now permit the real q1 AskUserAsync finally to execute. Only the production
            // ReferenceEquals guard can keep q2 alive at this point.
            await context.RunPostedAsync(TestContext.Current.CancellationToken);
            var firstResult = await firstAskTask.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await publishContinuation.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.Equal(
                cancelFirstQuestion
                    ? "User cancelled the question without answering."
                    : "answer-one",
                firstResult);
            Assert.Same(q2, composer.PendingQuestion);
            Assert.False(q2.Completion.Task.IsCompleted);

            // Clean up q2 through the same public actor route.
            composer.CancelQuestion();
            Assert.Equal(
                "User cancelled the question without answering.",
                await secondAskTask.WaitAsync(
                    TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Null(composer.PendingQuestion);
        }
        finally
        {
            if (composer.PendingQuestion is not null)
                composer.CancelQuestion();
            await composer.DisposeAsync();
            dbContext.Dispose();
            try
            {
                if (Directory.Exists(tmpDir))
                    Directory.Delete(tmpDir, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

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
    private readonly string? _previousStateDir;
    private CopilotHiveDbContext? _dbContext;

    public ComposerCompactPartialEndpointFactory()
    {
        _previousStateDir = Environment.GetEnvironmentVariable("STATE_DIR");
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
        Environment.SetEnvironmentVariable("STATE_DIR", _previousStateDir);
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