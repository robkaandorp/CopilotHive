using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using SharpCoder;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Integration tests for <see cref="Composer.SendMessageWithEvents"/> event-block prepending,
/// <see cref="Composer.TrySplitEventBlock"/> envelope splitting, and event restoration on
/// rejection. Uses a real <see cref="Composer"/> with a fake chat client injected via
/// reflection (same pattern as <see cref="ComposerStreamingServiceTests"/>).
/// </summary>
public sealed class ComposerEventIntegrationTests
{
    private const BindingFlags PrivateFlags =
        BindingFlags.Instance | BindingFlags.NonPublic;

    // ── Helpers ──

    /// <summary>
    /// Builds a length-delimited envelope-wrapped message exactly as
    /// <see cref="Composer.SendMessageWithEvents"/> does: the envelope header (with the
    /// invariant-culture event-block length) followed by the event block and closing
    /// <c>}}</c>, then the user message.
    /// </summary>
    private static string Wrap(string? eventBlock, string userMessage)
    {
        var eventLen = eventBlock?.Length ?? 0;
        return $"{Composer.EnvelopePrefix}{eventLen.ToString(CultureInfo.InvariantCulture)}{Composer.EnvelopeSeparator}{eventBlock ?? ""}{Composer.EnvelopeSuffix}{userMessage}";
    }

    /// <summary>
    /// Uses reflection to inject a fake <see cref="IChatClient"/> into a
    /// <see cref="Composer"/> instance and then rebuilds its internal
    /// <c>CodingAgent</c> by calling the private <c>RecreateAgentAsync()</c> method.
    /// </summary>
    private static async Task InjectFakeChatClient(Composer composer, IChatClient fakeClient)
    {
        var agentService = GetAgentService(composer);
        var serviceType = agentService.GetType();

        var chatClientField = serviceType.GetField("_chatClient", PrivateFlags)
            ?? throw new InvalidOperationException("_chatClient field not found on ComposerAgentService");
        chatClientField.SetValue(agentService, fakeClient);

        var recreateAgent = serviceType.GetMethod("RecreateAgentAsync",
            PrivateFlags | BindingFlags.Public)
            ?? throw new InvalidOperationException("RecreateAgentAsync method not found on ComposerAgentService");
        await (Task)recreateAgent.Invoke(agentService, null)!;
    }

    /// <summary>Gets the private <c>_agentService</c> instance from a <see cref="Composer"/>.</summary>
    private static object GetAgentService(Composer composer)
    {
        var field = typeof(Composer).GetField("_agentService", PrivateFlags)
            ?? throw new InvalidOperationException("_agentService field not found on Composer");
        return field.GetValue(composer)
            ?? throw new InvalidOperationException("_agentService was null");
    }

    /// <summary>Gets the private <c>_streamingService</c> instance from a <see cref="Composer"/>.</summary>
    private static ComposerStreamingService GetStreamingService(Composer composer)
    {
        var field = typeof(Composer).GetField("_streamingService", PrivateFlags)
            ?? throw new InvalidOperationException("_streamingService field not found on Composer");
        return (ComposerStreamingService)(field.GetValue(composer)
            ?? throw new InvalidOperationException("_streamingService was null"));
    }

    /// <summary>Gets the private <c>_session</c> field from the agent service.</summary>
    private static AgentSession GetSession(Composer composer)
    {
        var agentService = GetAgentService(composer);
        var sessionField = agentService.GetType().GetField("_session", PrivateFlags)
            ?? throw new InvalidOperationException("_session field not found on ComposerAgentService");
        return (AgentSession)sessionField.GetValue(agentService)!;
    }

    /// <summary>Creates a temp state directory for a test.</summary>
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Deterministic cleanup. Every step is attempted independently and any failure is
    /// aggregated and thrown so an incomplete cleanup fails the test instead of leaking silently.
    /// </summary>
    private static async Task CleanupAsync(Composer? composer, CopilotHiveDbContext? dbContext, string? tmpDir, IDisposable? disposable = null)
    {
        var cleanupErrors = new List<Exception>();

        if (disposable is not null)
        {
            try { disposable.Dispose(); }
            catch (Exception ex) { cleanupErrors.Add(ex); }
        }

        if (composer is not null)
        {
            try { await composer.DisposeAsync(); }
            catch (Exception ex) { cleanupErrors.Add(ex); }
        }

        if (dbContext is not null)
        {
            try { dbContext.Dispose(); }
            catch (Exception ex) { cleanupErrors.Add(ex); }
        }

        if (tmpDir is not null && Directory.Exists(tmpDir))
        {
            try { Directory.Delete(tmpDir, recursive: true); }
            catch (Exception ex) { cleanupErrors.Add(ex); }
        }

        if (tmpDir is not null && Directory.Exists(tmpDir))
            cleanupErrors.Add(new IOException($"Temp directory '{tmpDir}' still exists after cleanup"));

        if (cleanupErrors.Count > 0)
            throw new AggregateException("Cleanup failed", cleanupErrors);
    }

    /// <summary>
    /// Creates a <see cref="Composer"/> along with the in-memory <see cref="CopilotHiveDbContext"/>
    /// backing its goal store, optionally with an <see cref="ComposerEventSubscriber"/>.
    /// </summary>
    private static (Composer Composer, CopilotHiveDbContext DbContext, ComposerEventSubscriber? Subscriber) CreateComposer(
        string tmpDir,
        ComposerEventSubscriber? subscriber = null)
    {
        var dbContext = CopilotHiveDbContext.CreateInMemory();
        try
        {
            var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                store,
                stateDir: tmpDir,
                eventSubscriber: subscriber);
            return (composer, dbContext, subscriber);
        }
        catch
        {
            dbContext.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A streaming chat client that captures the last user message from the messages
    /// passed to <c>GetStreamingResponseAsync</c> and yields a completion update.
    /// </summary>
    private sealed class CapturingStreamingClient : IChatClient
    {
        private readonly string _replyText;
        public string? LastUserMessage { get; private set; }

        public CapturingStreamingClient(string replyText = "OK")
        {
            _replyText = replyText;
        }

        public ChatClientMetadata Metadata => new("stub", null, "stub-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CaptureLastUser(messages);
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, _replyText))
            {
                FinishReason = ChatFinishReason.Stop,
            };
            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            CaptureLastUser(messages);
            yield return new ChatResponseUpdate(ChatRole.Assistant, _replyText)
            {
                FinishReason = ChatFinishReason.Stop,
            };
        }

        private void CaptureLastUser(IEnumerable<ChatMessage> messages)
        {
            // The messages enumerable is the session history; the last user message
            // is the one Composer.SendMessageWithEvents formatted and sent.
            ChatMessage? lastUser = null;
            foreach (var m in messages)
            {
                if (m.Role == ChatRole.User)
                    lastUser = m;
            }
            LastUserMessage = lastUser?.Text;
        }

        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    /// <summary>
    /// Waits for streaming to complete by polling IsStreaming (synchronously, since
    /// the fake client completes immediately after Task.Yield).
    /// </summary>
    private static async Task WaitForStreamingCompleteAsync(ComposerStreamingService streamingService)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (streamingService.IsStreaming && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.False(streamingService.IsStreaming, "Streaming should have completed");
    }

    // ── 1. Events present → formatted block prepended before user message, inside envelope ──

    [Fact]
    public async Task SendMessage_WithPendingEvents_PrependsFormattedEventBlock()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        ComposerEventSubscriber? subscriber = null;
        var tmpDir = CreateTempDir();
        try
        {
            var bus = new EventBus();
            subscriber = new ComposerEventSubscriber(bus);
            (composer, dbContext, _) = CreateComposer(tmpDir, subscriber: subscriber);

            // Publish a GoalCompleted event.
            bus.Publish(new SystemEvent(EventType.GoalCompleted, "Goal merged successfully", GoalId: "my-goal"));

            var client = new CapturingStreamingClient();
            await InjectFakeChatClient(composer, client);

            composer.SendMessage("Please review the latest changes");

            var streamingService = GetStreamingService(composer);
            await WaitForStreamingCompleteAsync(streamingService);

            // The captured user message must be EXACTLY the envelope-wrapped event block
            // followed by the original user text — full-string equality, no fragments.
            var block =
                "[System Events since your last message]\n" +
                "- ✅ Goal 'my-goal' completed — Goal merged successfully";
            var expected = Wrap(block, "Please review the latest changes");
            Assert.NotNull(client.LastUserMessage);
            Assert.Equal(expected, client.LastUserMessage!);

            // The subscriber buffer must be cleared (events were drained).
            Assert.Empty(subscriber.PeekPendingEvents());
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir, subscriber);
        }
    }

    // ── 2. Multiple event types → each formatted per spec, inside envelope ──

    [Fact]
    public async Task SendMessage_WithMultipleEventTypes_FormatsEachPerSpec()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        ComposerEventSubscriber? subscriber = null;
        var tmpDir = CreateTempDir();
        try
        {
            var bus = new EventBus();
            subscriber = new ComposerEventSubscriber(bus);
            (composer, dbContext, _) = CreateComposer(tmpDir, subscriber: subscriber);

            bus.Publish(new SystemEvent(EventType.GoalCompleted, "Goal merged successfully", GoalId: "g-1"));
            bus.Publish(new SystemEvent(EventType.GoalFailed, "Build failed", GoalId: "g-2"));
            bus.Publish(new SystemEvent(EventType.GoalDispatched, "dispatched", GoalId: "g-3"));
            bus.Publish(new SystemEvent(EventType.IssueRaised, "Bug found", IssueId: "iss-1"));
            bus.Publish(new SystemEvent(EventType.IssueResolved, "", IssueId: "iss-2"));
            bus.Publish(new SystemEvent(EventType.ReleaseCompleted, "Released", ReleaseId: "r-1"));

            var client = new CapturingStreamingClient();
            await InjectFakeChatClient(composer, client);

            composer.SendMessage("check status");

            await WaitForStreamingCompleteAsync(GetStreamingService(composer));

            // Full-string equality: exact header, each event line in FIFO order with the exact
            // emoji, quoted ID, and em-dash, then the exact envelope suffix and original user text.
            var block =
                "[System Events since your last message]\n" +
                "- ✅ Goal 'g-1' completed — Goal merged successfully\n" +
                "- ❌ Goal 'g-2' failed — Build failed\n" +
                "- \uD83D\uDE80 Goal 'g-3' dispatched\n" +
                "- \uD83D\uDC1B Issue 'iss-1' raised — Bug found\n" +
                "- ✅ Issue 'iss-2' resolved\n" +
                "- \uD83D\uDCE6 Release 'r-1' completed — Released";
            var expected = Wrap(block, "check status");
            Assert.NotNull(client.LastUserMessage);
            Assert.Equal(expected, client.LastUserMessage!);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir, subscriber);
        }
    }

    // ── 2b. Formatter mapping: all six event types → exact block text ──

    [Fact]
    public async Task SendMessage_FormatterMapping_AllSixEventTypes_ProducesExactBlock()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        ComposerEventSubscriber? subscriber = null;
        var tmpDir = CreateTempDir();
        try
        {
            var bus = new EventBus();
            subscriber = new ComposerEventSubscriber(bus);
            (composer, dbContext, _) = CreateComposer(tmpDir, subscriber: subscriber);

            // Publish one event of each EventType in a fixed order.
            bus.Publish(new SystemEvent(EventType.GoalCompleted, "Goal merged successfully", GoalId: "goal-1"));
            bus.Publish(new SystemEvent(EventType.GoalFailed, "Build failed", GoalId: "goal-2"));
            bus.Publish(new SystemEvent(EventType.GoalDispatched, "dispatched", GoalId: "goal-3"));
            bus.Publish(new SystemEvent(EventType.IssueRaised, "Bug found", IssueId: "issue-1"));
            bus.Publish(new SystemEvent(EventType.IssueResolved, "", IssueId: "issue-2"));
            bus.Publish(new SystemEvent(EventType.ReleaseCompleted, "Released", ReleaseId: "release-1"));

            var client = new CapturingStreamingClient();
            await InjectFakeChatClient(composer, client);

            composer.SendMessage("user text");

            await WaitForStreamingCompleteAsync(GetStreamingService(composer));

            // The exact multi-line block — any change to an emoji, field reference, header
            // text, or separator in FormatEventBlock (or SendMessageWithEvents) must fail this test.
            var block =
                "[System Events since your last message]\n" +
                "- ✅ Goal 'goal-1' completed — Goal merged successfully\n" +
                "- ❌ Goal 'goal-2' failed — Build failed\n" +
                "- \uD83D\uDE80 Goal 'goal-3' dispatched\n" +
                "- \uD83D\uDC1B Issue 'issue-1' raised — Bug found\n" +
                "- ✅ Issue 'issue-2' resolved\n" +
                "- \uD83D\uDCE6 Release 'release-1' completed — Released";
            var expected = Wrap(block, "user text");
            Assert.NotNull(client.LastUserMessage);
            Assert.Equal(expected, client.LastUserMessage!);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir, subscriber);
        }
    }

    // ── 3. No events → message passed through, wrapped with E0 envelope ──

    [Fact]
    public async Task SendMessage_WithNoPendingEvents_WrapsWithEmptyEnvelope()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        ComposerEventSubscriber? subscriber = null;
        var tmpDir = CreateTempDir();
        try
        {
            var bus = new EventBus();
            subscriber = new ComposerEventSubscriber(bus);
            (composer, dbContext, _) = CreateComposer(tmpDir, subscriber: subscriber);

            // No events published — buffer is empty.

            var client = new CapturingStreamingClient();
            await InjectFakeChatClient(composer, client);

            const string original = "Just a regular message with no events";

            composer.SendMessage(original);

            await WaitForStreamingCompleteAsync(GetStreamingService(composer));

            // Every message is envelope-wrapped, even with zero events: {{CHV1:E0|}}<message>.
            Assert.NotNull(client.LastUserMessage);
            Assert.Equal(Wrap(null, original), client.LastUserMessage!);
            Assert.DoesNotContain("[System Events", client.LastUserMessage!);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir, subscriber);
        }
    }

    // ── 4. Rejected SendMessage → events restored to subscriber ──

    [Fact]
    public async Task SendMessage_WhenStreamingServiceThrows_RestoresEventsAndPropagates()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        ComposerEventSubscriber? subscriber = null;
        var tmpDir = CreateTempDir();
        try
        {
            var bus = new EventBus();
            subscriber = new ComposerEventSubscriber(bus);
            (composer, dbContext, _) = CreateComposer(tmpDir, subscriber: subscriber);

            // Publish events so they're buffered.
            bus.Publish(new SystemEvent(EventType.GoalCompleted, "Goal merged successfully", GoalId: "goal-A"));
            bus.Publish(new SystemEvent(EventType.GoalFailed, "Build broke", GoalId: "goal-B"));

            // Assert events are buffered before the send.
            Assert.Equal(2, subscriber.PeekPendingEvents().Count);

            // Do NOT inject a chat client — the agent is null, so _streamingService.SendMessage
            // will throw "Composer not connected". This exercises the rejection/restore path.
            var ex = Assert.Throws<InvalidOperationException>(() => composer.SendMessage("test message"));
            Assert.Contains("not connected", ex.Message, StringComparison.OrdinalIgnoreCase);

            // The drained events must have been restored to the subscriber buffer.
            var restored = subscriber.PeekPendingEvents();
            Assert.Equal(2, restored.Count);
            Assert.Equal("goal-A", restored[0].GoalId);
            Assert.Equal("goal-B", restored[1].GoalId);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir, subscriber);
        }
    }

    // ── 5. Subscriber null → no drain, message wrapped with E0 envelope ──

    [Fact]
    public async Task SendMessage_WithNullSubscriber_WrapsWithEmptyEnvelope()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        var tmpDir = CreateTempDir();
        try
        {
            // No subscriber — null is the default.
            (composer, dbContext, _) = CreateComposer(tmpDir);

            var client = new CapturingStreamingClient();
            await InjectFakeChatClient(composer, client);

            const string original = "Message with no subscriber attached";

            composer.SendMessage(original);

            await WaitForStreamingCompleteAsync(GetStreamingService(composer));

            // The message must be wrapped with the empty envelope (E0) even without a subscriber.
            Assert.NotNull(client.LastUserMessage);
            Assert.Equal(Wrap(null, original), client.LastUserMessage!);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 6. Rejected SendMessage with null subscriber → does not crash ──

    [Fact]
    public async Task SendMessage_WithNullSubscriberAndThrowingService_PropagatesWithoutRestore()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        var tmpDir = CreateTempDir();
        try
        {
            // No subscriber — null is the default.
            (composer, dbContext, _) = CreateComposer(tmpDir);

            // Agent is null → streaming service throws.
            var ex = Assert.Throws<InvalidOperationException>(() => composer.SendMessage("test"));
            Assert.Contains("not connected", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 7. SendMessageWithEvents: return value and envelope behavior ──

    [Fact]
    public async Task SendMessageWithEvents_WithPendingEvents_ReturnsExactEventBlock()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        ComposerEventSubscriber? subscriber = null;
        var tmpDir = CreateTempDir();
        try
        {
            var bus = new EventBus();
            subscriber = new ComposerEventSubscriber(bus);
            (composer, dbContext, _) = CreateComposer(tmpDir, subscriber: subscriber);

            bus.Publish(new SystemEvent(EventType.GoalCompleted, "Goal merged successfully", GoalId: "my-goal"));

            var client = new CapturingStreamingClient();
            await InjectFakeChatClient(composer, client);

            var result = composer.SendMessageWithEvents("Please review the latest changes");

            await WaitForStreamingCompleteAsync(GetStreamingService(composer));

            // The return value is the exact formatted event block.
            var block =
                "[System Events since your last message]\n" +
                "- ✅ Goal 'my-goal' completed — Goal merged successfully";
            Assert.Equal(block, result);

            // The captured message is the envelope-wrapped block + user text.
            Assert.Equal(Wrap(block, "Please review the latest changes"), client.LastUserMessage);

            // Events were drained.
            Assert.Empty(subscriber.PeekPendingEvents());
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir, subscriber);
        }
    }

    [Fact]
    public async Task SendMessageWithEvents_WithNoPendingEvents_ReturnsNull_AndWrapsWithE0()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        ComposerEventSubscriber? subscriber = null;
        var tmpDir = CreateTempDir();
        try
        {
            var bus = new EventBus();
            subscriber = new ComposerEventSubscriber(bus);
            (composer, dbContext, _) = CreateComposer(tmpDir, subscriber: subscriber);

            var client = new CapturingStreamingClient();
            await InjectFakeChatClient(composer, client);

            const string original = "no events here";
            var result = composer.SendMessageWithEvents(original);

            await WaitForStreamingCompleteAsync(GetStreamingService(composer));

            // No pending events → return null, but the message is still wrapped with E0|}}.
            Assert.Null(result);
            Assert.Equal(Wrap(null, original), client.LastUserMessage);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir, subscriber);
        }
    }

    [Fact]
    public async Task SendMessageWithEvents_WhenNotConnected_Throws_RestoresEventsAndReThrows()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        ComposerEventSubscriber? subscriber = null;
        var tmpDir = CreateTempDir();
        try
        {
            var bus = new EventBus();
            subscriber = new ComposerEventSubscriber(bus);
            (composer, dbContext, _) = CreateComposer(tmpDir, subscriber: subscriber);

            // Publish events so they're buffered.
            bus.Publish(new SystemEvent(EventType.GoalCompleted, "Goal merged successfully", GoalId: "goal-A"));
            bus.Publish(new SystemEvent(EventType.GoalFailed, "Build broke", GoalId: "goal-B"));
            Assert.Equal(2, subscriber.PeekPendingEvents().Count);

            // Do NOT inject a chat client — the agent is null, so _streamingService.SendMessage
            // throws. SendMessageWithEvents must restore the drained events and re-throw.
            var ex = Assert.Throws<InvalidOperationException>(() => composer.SendMessageWithEvents("test message"));
            Assert.Contains("not connected", ex.Message, StringComparison.OrdinalIgnoreCase);

            var restored = subscriber.PeekPendingEvents();
            Assert.Equal(2, restored.Count);
            Assert.Equal("goal-A", restored[0].GoalId);
            Assert.Equal("goal-B", restored[1].GoalId);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir, subscriber);
        }
    }

    [Fact]
    public async Task SendMessage_Wrapper_SendsEnvelopedMessageWithEvents()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        ComposerEventSubscriber? subscriber = null;
        var tmpDir = CreateTempDir();
        try
        {
            var bus = new EventBus();
            subscriber = new ComposerEventSubscriber(bus);
            (composer, dbContext, _) = CreateComposer(tmpDir, subscriber: subscriber);

            bus.Publish(new SystemEvent(EventType.GoalCompleted, "Done", GoalId: "g-1"));

            var client = new CapturingStreamingClient();
            await InjectFakeChatClient(composer, client);

            // SendMessage is a void wrapper — its return value is never used.
            composer.SendMessage("hello");

            await WaitForStreamingCompleteAsync(GetStreamingService(composer));

            var block =
                "[System Events since your last message]\n" +
                "- ✅ Goal 'g-1' completed — Done";
            Assert.Equal(Wrap(block, "hello"), client.LastUserMessage);
            Assert.Empty(subscriber.PeekPendingEvents());
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir, subscriber);
        }
    }

    // ── 8. TrySplitEventBlock: envelope splitting ──

    [Fact]
    public void TrySplitEventBlock_EnvelopeWithEvents_SplitsIntoEventBlockAndUserMessage()
    {
        var block =
            "[System Events since your last message]\n" +
            "- ✅ Goal 'g' completed — done";
        var content = Wrap(block, "user message text");

        var (eventBlock, userMessage) = Composer.TrySplitEventBlock(content);

        Assert.Equal(block, eventBlock);
        Assert.Equal("user message text", userMessage);
    }

    [Fact]
    public void TrySplitEventBlock_EnvelopeWithE0_ReturnsNullEventBlockAndUserMessage()
    {
        var content = "{{CHV1:E0|}}just a message";

        var (eventBlock, userMessage) = Composer.TrySplitEventBlock(content);

        Assert.Null(eventBlock);
        Assert.Equal("just a message", userMessage);
    }

    [Fact]
    public void TrySplitEventBlock_LegacyPlainUserMessage_ReturnsNullAndOriginalContent()
    {
        const string content = "plain legacy message without envelope";

        var (eventBlock, userMessage) = Composer.TrySplitEventBlock(content);

        Assert.Null(eventBlock);
        Assert.Equal(content, userMessage);
    }

    [Fact]
    public async Task TrySplitEventBlock_ValidPrefixCollision_RoundTripsThroughSendMessageWithEvents()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        ComposerEventSubscriber? subscriber = null;
        var tmpDir = CreateTempDir();
        try
        {
            var bus = new EventBus();
            subscriber = new ComposerEventSubscriber(bus);
            (composer, dbContext, _) = CreateComposer(tmpDir, subscriber: subscriber);

            var client = new CapturingStreamingClient();
            await InjectFakeChatClient(composer, client);

            // The user sends text that looks like an envelope prefix as literal content.
            const string literal = "{{CHV1:E3|abc}}hello";
            var result = composer.SendMessageWithEvents(literal);

            await WaitForStreamingCompleteAsync(GetStreamingService(composer));

            // No pending events → null, and the message is wrapped with an E0 envelope so the
            // literal prefix is protected: {{CHV1:E0|}}{{CHV1:E3|abc}}hello.
            Assert.Null(result);
            Assert.Equal(Wrap(null, literal), client.LastUserMessage);

            // Splitting the wrapped message must NOT misclassify the literal prefix: the E0
            // envelope is consumed and the literal text is returned as the user message.
            var (eventBlock, userMessage) = Composer.TrySplitEventBlock(client.LastUserMessage!);
            Assert.Null(eventBlock);
            Assert.Equal(literal, userMessage);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir, subscriber);
        }
    }

    [Fact]
    public void TrySplitEventBlock_Malformed_PrefixButNoSeparator_ReturnsNullAndContent()
    {
        const string content = "{{CHV1:E0}}no separator";

        var (eventBlock, userMessage) = Composer.TrySplitEventBlock(content);

        Assert.Null(eventBlock);
        Assert.Equal(content, userMessage);
    }

    [Fact]
    public void TrySplitEventBlock_Malformed_OversizedLength_ReturnsNullAndContent()
    {
        const string content = "{{CHV1:E999999999|abc}}hello";

        var (eventBlock, userMessage) = Composer.TrySplitEventBlock(content);

        Assert.Null(eventBlock);
        Assert.Equal(content, userMessage);
    }

    [Fact]
    public void TrySplitEventBlock_EventBlockContainingMarkers_SplitsCorrectly()
    {
        // The block contains the envelope suffix and separator as literal text; because the
        // envelope is length-delimited, splitting must still be exact.
        var block =
            "[System Events since your last message]\n" +
            "- ✅ Goal 'x' done — contains }} and | markers";
        var content = Wrap(block, "user text");

        var (eventBlock, userMessage) = Composer.TrySplitEventBlock(content);

        Assert.Equal(block, eventBlock);
        Assert.Equal("user text", userMessage);
    }

    [Fact]
    public void TrySplitEventBlock_EmptyUserMessageAfterEnvelope_ReturnsEventBlockAndEmpty()
    {
        var block =
            "[System Events since your last message]\n" +
            "- ✅ Goal 'x' completed — done";
        var content = Wrap(block, "");

        var (eventBlock, userMessage) = Composer.TrySplitEventBlock(content);

        Assert.Equal(block, eventBlock);
        Assert.Equal("", userMessage);
    }

    [Fact]
    public void TrySplitEventBlock_RoundTrip_ArbitraryEventTextAndUserText_SplitsCorrectly()
    {
        var block =
            "[System Events since your last message]\n" +
            "- ✅ Goal 'multi' completed — line one\n" +
            "- ❌ Goal 'multi' failed — line two with }} and |";
        var userText = "arbitrary user text\nwith newlines and }} markers";
        var content = Wrap(block, userText);

        var (eventBlock, userMessage) = Composer.TrySplitEventBlock(content);

        Assert.Equal(block, eventBlock);
        Assert.Equal(userText, userMessage);
    }
}
