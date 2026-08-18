using System.Reflection;
using System.Runtime.CompilerServices;

using CopilotHive.Dashboard;
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
/// Tests for the Composer facade's streaming admission gate and "Composer not available"
/// rejection paths introduced by the Phase 1 actor refactor. These verify the facade-level
/// guards (Interlocked.CompareExchange, not-connected check, Tell-fails rollback) that are
/// NOT covered by the actor-level tests (which test the actor in isolation).
/// </summary>
public sealed class ComposerFacadeGateTests
{
    private const BindingFlags PrivateFlags =
        BindingFlags.Instance | BindingFlags.NonPublic;

    // ── Helpers ──

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

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

    private static object GetAgentService(Composer composer)
    {
        var field = typeof(Composer).GetField("_agentService", PrivateFlags)
            ?? throw new InvalidOperationException("_agentService field not found on Composer");
        return field.GetValue(composer)
            ?? throw new InvalidOperationException("_agentService was null");
    }

    private static (Composer Composer, CopilotHiveDbContext DbContext) CreateComposer(string tmpDir)
    {
        var dbContext = CopilotHiveDbContext.CreateInMemory();
        try
        {
            var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
            var composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                store,
                stateDir: tmpDir);
            return (composer, dbContext);
        }
        catch
        {
            dbContext.Dispose();
            throw;
        }
    }

    private static async Task CleanupAsync(Composer? composer, CopilotHiveDbContext? dbContext, string? tmpDir)
    {
        var errors = new List<Exception>();
        if (composer is not null)
        {
            try { await composer.DisposeAsync(); }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (dbContext is not null)
        {
            try { dbContext.Dispose(); }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (tmpDir is not null && Directory.Exists(tmpDir))
        {
            try { Directory.Delete(tmpDir, recursive: true); }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (errors.Count > 0)
            throw new AggregateException("Cleanup failed", errors);
    }

    /// <summary>
    /// A streaming chat client that blocks on a semaphore until released, letting tests
    /// hold a stream open deterministically.
    /// </summary>
    private sealed class BlockingStreamingClient : IChatClient
    {
        private readonly SemaphoreSlim _gate = new(0, 1);

        public ChatClientMetadata Metadata => new("blocking", null, "blocking-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            await _gate.WaitAsync(cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done")
            {
                FinishReason = ChatFinishReason.Stop,
            };
        }

        public void Release() => _gate.Release();

        public void Dispose() => _gate.Dispose();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    // ── 1. SendMessageWithEvents "already streaming" gate ──

    [Fact]
    public async Task SendMessageWithEvents_AlreadyStreaming_ThrowsAndDoesNotDrainEvents()
    {
        var tmpDir = CreateTempDir();
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);
            var blockingClient = new BlockingStreamingClient();
            await InjectFakeChatClient(composer, blockingClient);

            // Start a stream — it blocks on the semaphore.
            composer.SendMessage("first");

            // Wait until streaming is active.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!composer.IsStreaming && DateTime.UtcNow < deadline)
                await Task.Delay(20, TestContext.Current.CancellationToken);
            Assert.True(composer.IsStreaming, "First send should have started streaming");

            // Second send while streaming must be rejected by the facade's admission gate.
            var ex = Assert.Throws<InvalidOperationException>(
                () => composer.SendMessageWithEvents("second"));
            Assert.Contains("already streaming", ex.Message, StringComparison.OrdinalIgnoreCase);

            // The gate must still be held by the first stream.
            Assert.True(composer.IsStreaming, "Gate should still be held by first stream");

            // Cleanup: cancel and release.
            composer.CancelStreaming();
            blockingClient.Release();
            deadline = DateTime.UtcNow.AddSeconds(10);
            while (composer.IsStreaming && DateTime.UtcNow < deadline)
                await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 2. SendMessageWithEvents not-connected check fires before admission ──

    [Fact]
    public async Task SendMessageWithEvents_NotConnected_ThrowsBeforeAdmission()
    {
        var tmpDir = CreateTempDir();
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);
            // Do NOT inject a chat client — not connected.
            Assert.False(composer.IsConnected);

            var ex = Assert.Throws<InvalidOperationException>(
                () => composer.SendMessageWithEvents("test"));
            Assert.Contains("not connected", ex.Message, StringComparison.OrdinalIgnoreCase);

            // The admission gate must NOT be held (the check is before CompareExchange).
            Assert.False(composer.IsStreaming, "Gate should not be held when not-connected check fires");
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 3. ResetSessionAsync "while streaming" facade-level rejection ──

    [Fact]
    public async Task ResetSessionAsync_WhileStreaming_FacadeRejects()
    {
        var tmpDir = CreateTempDir();
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);

            // Simulate an active stream via the test seam.
            composer.SetStreamingStateForTest(true);
            try
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => composer.ResetSessionAsync(TestContext.Current.CancellationToken));
                Assert.Contains("Cannot reset while streaming", ex.Message);
            }
            finally
            {
                composer.SetStreamingStateForTest(false);
            }
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 4. SwitchModelAsync "while streaming" facade-level rejection ──

    [Fact]
    public async Task SwitchModelAsync_WhileStreaming_FacadeRejects()
    {
        var tmpDir = CreateTempDir();
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);

            // Simulate an active stream via the test seam.
            composer.SetStreamingStateForTest(true);
            try
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => composer.SwitchModelAsync("test-model", ReasoningEffort.High, TestContext.Current.CancellationToken));
                Assert.Contains("Cannot switch model while streaming", ex.Message);
            }
            finally
            {
                composer.SetStreamingStateForTest(false);
            }
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 5. SendMessageWithEvents "already streaming" gate is reset on Tell-failure rollback ──

    [Fact]
    public async Task SendMessageWithEvents_TellFails_GateResetAndEventsRestored()
    {
        var tmpDir = CreateTempDir();
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);
            var mockClient = new Mock<IChatClient>();
            mockClient
                .Setup(c => c.GetStreamingResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .Returns<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((_, _, _) => EmptyStream());
            mockClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
            await InjectFakeChatClient(composer, mockClient.Object);

            // Dispose the underlying actor to make Tell fail, then try to send.
            // We need to get the actor and dispose it without disposing the whole Composer.
            var actorField = typeof(Composer).GetField("_actor", PrivateFlags)
                ?? throw new InvalidOperationException("_actor field not found");
            var actor = actorField.GetValue(composer)!;
            var actorDispose = actor.GetType().GetMethod("DisposeAsync")
                ?? throw new InvalidOperationException("DisposeAsync not found on actor");
            await (ValueTask)actorDispose.Invoke(actor, null)!;

            // Now Tell will return false — the facade must roll back the gate.
            var ex = Assert.Throws<InvalidOperationException>(
                () => composer.SendMessageWithEvents("test"));
            Assert.Contains("not available", ex.Message, StringComparison.OrdinalIgnoreCase);

            // Gate must be released after rollback.
            Assert.False(composer.IsStreaming, "Gate must be reset after Tell-failure rollback");
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 6. ConnectAsync "Composer not available" when actor is disposed ──

    [Fact]
    public async Task ConnectAsync_ActorDisposed_ThrowsComposerNotAvailable()
    {
        var tmpDir = CreateTempDir();
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);

            // Dispose the underlying actor to make Tell fail.
            var actorField = typeof(Composer).GetField("_actor", PrivateFlags)
                ?? throw new InvalidOperationException("_actor field not found");
            var actor = actorField.GetValue(composer)!;
            var actorDispose = actor.GetType().GetMethod("DisposeAsync")
                ?? throw new InvalidOperationException("DisposeAsync not found on actor");
            await (ValueTask)actorDispose.Invoke(actor, null)!;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => composer.ConnectAsync(TestContext.Current.CancellationToken));
            Assert.Contains("not available", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 7. ResetSessionAsync "Composer not available" when actor is disposed ──

    [Fact]
    public async Task ResetSessionAsync_ActorDisposed_ThrowsComposerNotAvailable()
    {
        var tmpDir = CreateTempDir();
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);

            // Dispose the underlying actor to make Tell fail.
            var actorField = typeof(Composer).GetField("_actor", PrivateFlags)
                ?? throw new InvalidOperationException("_actor field not found");
            var actor = actorField.GetValue(composer)!;
            var actorDispose = actor.GetType().GetMethod("DisposeAsync")
                ?? throw new InvalidOperationException("DisposeAsync not found on actor");
            await (ValueTask)actorDispose.Invoke(actor, null)!;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => composer.ResetSessionAsync(TestContext.Current.CancellationToken));
            Assert.Contains("not available", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 8. SwitchModelAsync "Composer not available" when actor is disposed ──

    [Fact]
    public async Task SwitchModelAsync_ActorDisposed_ThrowsComposerNotAvailable()
    {
        var tmpDir = CreateTempDir();
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);

            // Dispose the underlying actor to make Tell fail.
            var actorField = typeof(Composer).GetField("_actor", PrivateFlags)
                ?? throw new InvalidOperationException("_actor field not found");
            var actor = actorField.GetValue(composer)!;
            var actorDispose = actor.GetType().GetMethod("DisposeAsync")
                ?? throw new InvalidOperationException("DisposeAsync not found on actor");
            await (ValueTask)actorDispose.Invoke(actor, null)!;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => composer.SwitchModelAsync("test-model", ReasoningEffort.Medium, TestContext.Current.CancellationToken));
            Assert.Contains("not available", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 9. CompactSessionAsync "Composer not available" when actor is disposed ──

    [Fact]
    public async Task CompactSessionAsync_ActorDisposed_ThrowsComposerNotAvailable()
    {
        var tmpDir = CreateTempDir();
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);
            var mockClient = new Mock<IChatClient>();
            mockClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
            mockClient
                .Setup(c => c.GetStreamingResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .Returns<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((_, _, _) => EmptyStream());
            await InjectFakeChatClient(composer, mockClient.Object);

            // Dispose the underlying actor to make Tell fail.
            var actorField = typeof(Composer).GetField("_actor", PrivateFlags)
                ?? throw new InvalidOperationException("_actor field not found");
            var actor = actorField.GetValue(composer)!;
            var actorDispose = actor.GetType().GetMethod("DisposeAsync")
                ?? throw new InvalidOperationException("DisposeAsync not found on actor");
            await (ValueTask)actorDispose.Invoke(actor, null)!;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => composer.CompactSessionAsync(TestContext.Current.CancellationToken));
            Assert.Contains("not available", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 10. CompactOldestPercentAsync "Composer not available" when actor is disposed ──

    [Fact]
    public async Task CompactOldestPercentAsync_ActorDisposed_ThrowsComposerNotAvailable()
    {
        var tmpDir = CreateTempDir();
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);
            var mockClient = new Mock<IChatClient>();
            mockClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
            mockClient
                .Setup(c => c.GetStreamingResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .Returns<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((_, _, _) => EmptyStream());
            await InjectFakeChatClient(composer, mockClient.Object);

            // Dispose the underlying actor to make Tell fail.
            var actorField = typeof(Composer).GetField("_actor", PrivateFlags)
                ?? throw new InvalidOperationException("_actor field not found");
            var actor = actorField.GetValue(composer)!;
            var actorDispose = actor.GetType().GetMethod("DisposeAsync")
                ?? throw new InvalidOperationException("DisposeAsync not found on actor");
            await (ValueTask)actorDispose.Invoke(actor, null)!;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => composer.CompactOldestPercentAsync(50, TestContext.Current.CancellationToken));
            Assert.Contains("not available", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 11. SendMessageWithEvents admission gate rollback on generic exception ──

    [Fact]
    public async Task SendMessageWithEvents_UpdateCallbackThrows_GateReset()
    {
        var tmpDir = CreateTempDir();
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);
            await InjectFakeChatClient(composer, CreateNoOpClient());

            // After admission the facade raises OnStreamingUpdate. A throwing subscriber must
            // be rolled back by the single catch-all so the gate is released.
            composer.OnStreamingUpdate += () => throw new ArgumentException("UI callback boom");

            var thrownEx = Assert.Throws<ArgumentException>(
                () => composer.SendMessageWithEvents("test"));
            Assert.Contains("UI callback boom", thrownEx.Message);

            // Gate must be released after rollback.
            Assert.False(composer.IsStreaming, "Gate must be reset after generic-exception rollback");
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 12. Admitted InvalidOperationException also rolls back (defect 3) ──

    /// <summary>
    /// An <see cref="InvalidOperationException"/> thrown AFTER admission (here from an
    /// <c>OnStreamingUpdate</c> subscriber) must take the same rollback path as any other
    /// failure. With a special-cased `catch (InvalidOperationException) { throw; }` the gate
    /// stays latched at 1 and the Composer can never stream again.
    /// </summary>
    [Fact]
    public async Task SendMessageWithEvents_AdmittedInvalidOperationException_GateResetAndSendableAgain()
    {
        var tmpDir = CreateTempDir();
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);
            await InjectFakeChatClient(composer, CreateNoOpClient());

            var shouldThrow = true;
            void Handler()
            {
                if (shouldThrow) throw new InvalidOperationException("admitted IOE boom");
            }
            composer.OnStreamingUpdate += Handler;

            var ex = Assert.Throws<InvalidOperationException>(
                () => composer.SendMessageWithEvents("first"));
            Assert.Contains("admitted IOE boom", ex.Message);

            // The gate must be released — this is the whole point of the fix.
            Assert.False(composer.IsStreaming,
                "An admitted InvalidOperationException must roll back the admission gate");

            // Removal-proof: a subsequent send must be ADMITTED (it would throw
            // "already streaming" if the gate were still latched).
            shouldThrow = false;
            var result = composer.SendMessageWithEvents("second");
            Assert.Null(result); // no pending events

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (composer.IsStreaming && DateTime.UtcNow < deadline)
                await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    /// <summary>
    /// The same rollback must restore drained events, so a failed send never loses the
    /// pending event buffer.
    /// </summary>
    [Fact]
    public async Task SendMessageWithEvents_AdmittedFailureAfterDrain_RestoresEvents()
    {
        var tmpDir = CreateTempDir();
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        ComposerEventSubscriber? subscriber = null;
        try
        {
            dbContext = CopilotHiveDbContext.CreateInMemory();
            var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
            var bus = new EventBus();
            subscriber = new ComposerEventSubscriber(bus);
            composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                store,
                stateDir: tmpDir,
                eventSubscriber: subscriber);

            await InjectFakeChatClient(composer, CreateNoOpClient());

            bus.Publish(new SystemEvent(EventType.GoalCompleted, "done", GoalId: "goal-A"));
            bus.Publish(new SystemEvent(EventType.GoalFailed, "broke", GoalId: "goal-B"));
            Assert.Equal(2, subscriber.PeekPendingEvents().Count);

            // The Tell-failure branch throws InvalidOperationException AFTER events are drained.
            var actorField = typeof(Composer).GetField("_actor", PrivateFlags)!;
            var actor = actorField.GetValue(composer)!;
            var actorDispose = actor.GetType().GetMethod("DisposeAsync")!;
            await (ValueTask)actorDispose.Invoke(actor, null)!;

            var ex = Assert.Throws<InvalidOperationException>(
                () => composer.SendMessageWithEvents("test"));
            Assert.Contains("not available", ex.Message, StringComparison.OrdinalIgnoreCase);

            // Gate released AND events restored by the single catch-all rollback.
            Assert.False(composer.IsStreaming, "Gate must be reset after the Tell-failure rollback");
            var restored = subscriber.PeekPendingEvents();
            Assert.Equal(2, restored.Count);
            Assert.Equal("goal-A", restored[0].GoalId);
            Assert.Equal("goal-B", restored[1].GoalId);
        }
        finally
        {
            subscriber?.Dispose();
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 13. Reset/switch cancellation is authoritative (defect 4) ──

    /// <summary>
    /// The facade must await the reply itself rather than <c>WaitAsync(ct)</c>: the actor owns
    /// the operation and classifies caller cancellation, so the caller observes a real
    /// <see cref="OperationCanceledException"/> from the reply — not an abandoned wait that
    /// leaves the actor still mutating state.
    /// </summary>
    [Fact]
    public async Task SwitchModelAsync_CallerCancelled_ObservesCancellationFromReply()
    {
        var tmpDir = CreateTempDir();
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);
            await InjectFakeChatClient(composer, CreateNoOpClient());

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => composer.SwitchModelAsync("test-model", ReasoningEffort.High, cts.Token));

            // The model is unchanged — the actor rejected the switch, and the facade observed
            // that authoritative outcome rather than abandoning the wait.
            Assert.Equal("test-model", composer.GetStats()?.Model);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    /// <summary>
    /// The facade's reset must complete the FULL sequence (actor reset + facade cleanup) and
    /// never leave a half-applied state, so a successful reset clears the session file.
    /// </summary>
    [Fact]
    public async Task ResetSessionAsync_Succeeds_CompletesFacadeCleanup()
    {
        var tmpDir = CreateTempDir();
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);
            await InjectFakeChatClient(composer, CreateNoOpClient());

            var sessionFile = Path.Combine(tmpDir, "composer-session.json");
            await File.WriteAllTextAsync(sessionFile, "{}", TestContext.Current.CancellationToken);
            Assert.True(File.Exists(sessionFile));

            await composer.ResetSessionAsync(TestContext.Current.CancellationToken);

            // The facade cleanup after the authoritative wait must have run.
            Assert.False(File.Exists(sessionFile), "Reset must delete the persisted session file");
            Assert.False(composer.IsCompacting);
            Assert.False(composer.WasCompacted);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    /// <summary>
    /// A pre-cancelled reset must be fully atomic at the facade level: the actor consumes the
    /// token and cancels the reply, so the facade's <c>await reply.Task</c> throws and NONE of
    /// the facade-only post-reset steps run — no attachment clear, no session-file deletion, no
    /// registry refresh, no compaction-flag reset. Nothing is half-applied.
    /// </summary>
    [Fact]
    public async Task ResetSessionAsync_PreCancelledToken_ThrowsAndSkipsAllFacadeCleanup()
    {
        var tmpDir = CreateTempDir();
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        ComposerAttachmentService? attachments = null;
        try
        {
            dbContext = CopilotHiveDbContext.CreateInMemory();
            var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
            var registry = new LlmSessionRegistry();
            attachments = new ComposerAttachmentService(
                tmpDir, NullLogger<ComposerAttachmentService>.Instance);

            composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                store,
                stateDir: tmpDir,
                sessionRegistry: registry,
                attachmentService: attachments);

            await InjectFakeChatClient(composer, CreateNoOpClient());

            // Seed every observable the facade cleanup would touch.
            var saved = await attachments.SaveAsync(
                "diagram.png",
                new MemoryStream([0x01, 0x02, 0x03]),
                TestContext.Current.CancellationToken);
            Assert.True(saved.Success);

            var sessionFile = Path.Combine(tmpDir, "composer-session.json");
            await File.WriteAllTextAsync(sessionFile, "{}", TestContext.Current.CancellationToken);

            var registryCountBefore = registry.GetAll().Count(s => s.SessionId == "composer");

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            // (c) The facade surfaces the cancellation from the authoritative reply.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => composer.ResetSessionAsync(cts.Token));

            // (d) NO facade-only post-reset step ran.
            Assert.True(File.Exists(sessionFile),
                "A cancelled reset must NOT delete the session file");
            Assert.Single(Directory.GetFiles(attachments.AttachmentsRootPath));
            Assert.Equal(registryCountBefore, registry.GetAll().Count(s => s.SessionId == "composer"));
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 14. Disposal always releases the issue lock (defect 9) ──

    /// <summary>
    /// The issue-update lock must be disposed even when agent disposal throws. Without the
    /// try/finally the lock leaks whenever the agent service fails to dispose.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_AgentDisposalThrows_StillDisposesIssueLock()
    {
        var tmpDir = CreateTempDir();
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);

            // A chat client whose disposal throws makes _agentService.DisposeAsync() fail.
            await InjectFakeChatClient(composer, new ThrowingDisposeChatClient());

            var lockField = typeof(Composer).GetField("_issueUpdateLock", PrivateFlags)!;
            var issueLock = (SemaphoreSlim)lockField.GetValue(composer)!;

            // The agent failure is surfaced (not swallowed)…
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await composer.DisposeAsync());

            // …and the lock is still disposed.
            Assert.Throws<ObjectDisposedException>(() => _ = issueLock.AvailableWaitHandle);

            composer = null; // already disposed
        }
        finally
        {
            if (composer is not null)
            {
                try { await composer.DisposeAsync(); }
                catch (Exception) { /* best effort */ }
            }
            dbContext?.Dispose();
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    /// <summary>
    /// A clean disposal disposes the lock exactly once via the same finally path.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_CleanPath_DisposesIssueLock()
    {
        var tmpDir = CreateTempDir();
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);
            await InjectFakeChatClient(composer, CreateNoOpClient());

            var lockField = typeof(Composer).GetField("_issueUpdateLock", PrivateFlags)!;
            var issueLock = (SemaphoreSlim)lockField.GetValue(composer)!;

            await composer.DisposeAsync();
            composer = null;

            Assert.Throws<ObjectDisposedException>(() => _ = issueLock.AvailableWaitHandle);
        }
        finally
        {
            if (composer is not null)
            {
                try { await composer.DisposeAsync(); }
                catch (Exception) { /* best effort */ }
            }
            dbContext?.Dispose();
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    /// <summary>
    /// When actor disposal times out, actor-dependent resources (the agent service AND the
    /// issue lock) must be DEFERRED until the loop actually exits — disposing them while the
    /// streaming task can still use them is a use-after-dispose.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ActorTimesOut_DefersIssueLockUntilCompletion()
    {
        var tmpDir = CreateTempDir();
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);

            // A stream that ignores the cancellation token keeps the actor's loop busy past
            // the 5-second dispose timeout, so DisposeAsync takes the deferred branch.
            await InjectFakeChatClient(composer, new UncancellableStreamingClient(release.Task));

            var lockField = typeof(Composer).GetField("_issueUpdateLock", PrivateFlags)!;
            var issueLock = (SemaphoreSlim)lockField.GetValue(composer)!;

            composer.SendMessage("hello");

            var actorField = typeof(Composer).GetField("_actor", PrivateFlags)!;
            var actor = actorField.GetValue(composer)!;
            var completionProp = actor.GetType().GetProperty("Completion")!;
            var actorTaskField = actor.GetType().GetField("_streamingTask", PrivateFlags)!;

            // Wait for the actor's _streamingTask to be ASSIGNED, not merely for _isStreaming.
            // The send handler sets _isStreaming BEFORE creating the task, so polling the flag
            // from this thread can observe that window; disposing there would snapshot a null
            // task, complete immediately, and dispose the lock — a test-only race (in
            // production the send handler and OnShutdownAsync both run on the mailbox loop and
            // cannot interleave).
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (actorTaskField.GetValue(actor) is null && DateTime.UtcNow < deadline)
                await Task.Delay(20, TestContext.Current.CancellationToken);
            Assert.NotNull(actorTaskField.GetValue(actor));

            await composer.DisposeAsync();

            // Timed out: the lock must NOT have been disposed yet (the actor may still use it).
            // AvailableWaitHandle throws only once disposed — reading it proves the lock is alive.
            Assert.NotNull(issueLock.AvailableWaitHandle);

            // Let the stream finish so the actor loop exits and the deferred cleanup runs.
            release.TrySetResult();
            var completion = (Task)completionProp.GetValue(actor)!;
            await completion.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

            // The deferred continuation disposes the lock once the loop has exited.
            var disposeDeadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < disposeDeadline)
            {
                try { _ = issueLock.AvailableWaitHandle; }
                catch (ObjectDisposedException) { break; }
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            Assert.Throws<ObjectDisposedException>(() => _ = issueLock.AvailableWaitHandle);

            composer = null; // already disposed
        }
        finally
        {
            release.TrySetResult();
            if (composer is not null)
            {
                try { await composer.DisposeAsync(); }
                catch (Exception) { /* best effort */ }
            }
            dbContext?.Dispose();
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    /// <summary>Chat client returning a single completed response; used where streaming is incidental.</summary>
    private static IChatClient CreateNoOpClient()
    {
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((_, _, _) => EmptyStream());
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        return mockClient.Object;
    }

    /// <summary>Chat client whose disposal throws, exercising the agent-disposal failure path.</summary>
    private sealed class ThrowingDisposeChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("throwing-dispose", null, "throwing-dispose-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => EmptyStream();

        public void Dispose() => throw new InvalidOperationException("client dispose boom");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    /// <summary>
    /// Chat client whose stream ignores the cancellation token entirely, so actor disposal
    /// cannot complete within its 5-second timeout.
    /// </summary>
    private sealed class UncancellableStreamingClient(Task release) : IChatClient
    {
        public ChatClientMetadata Metadata => new("uncancellable", null, "uncancellable-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            // Deliberately does NOT observe cancellationToken.
            await release;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done")
            {
                FinishReason = ChatFinishReason.Stop,
            };
        }

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    /// <summary>Returns an empty async enumerable of chat updates.</summary>
    private static async IAsyncEnumerable<ChatResponseUpdate> EmptyStream()
    {
        await Task.Yield();
        yield break;
    }
}