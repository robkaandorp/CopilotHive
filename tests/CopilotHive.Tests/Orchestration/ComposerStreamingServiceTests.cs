using System.Reflection;
using System.Runtime.CompilerServices;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SharpCoder;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Integration tests for <see cref="ComposerStreamingService"/> — the extracted streaming
/// response loop. Tests verify error paths, callback invocation, cancellation, and disposal
/// by exercising the service through a real <see cref="Composer"/> with a fake chat client
/// injected via reflection.
/// </summary>
public sealed class ComposerStreamingServiceTests
{
    private const BindingFlags PrivateFlags =
        BindingFlags.Instance | BindingFlags.NonPublic;

    // ── Helpers ──

    /// <summary>
    /// Uses reflection to inject a fake <see cref="IChatClient"/> into a
    /// <see cref="Composer"/> instance and then rebuilds its internal
    /// <c>CodingAgent</c> by calling <c>RecreateAgentAsync()</c>.
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

    /// <summary>Creates a temp state directory for a test.</summary>
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Creates a <see cref="Composer"/> along with the in-memory <see cref="CopilotHiveDbContext"/>
    /// backing its goal store. Callers own both and must dispose them via <see cref="CleanupAsync"/>.
    /// </summary>
    private static (Composer Composer, CopilotHiveDbContext DbContext) CreateComposer(
        string tmpDir,
        LlmSessionRegistry? sessionRegistry = null)
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
                sessionRegistry: sessionRegistry);
            return (composer, dbContext);
        }
        catch
        {
            dbContext.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Deterministic cleanup. Every step is attempted independently and any failure is
    /// aggregated and thrown so an incomplete cleanup fails the test instead of leaking silently.
    /// </summary>
    private static async Task CleanupAsync(Composer? composer, CopilotHiveDbContext? dbContext, string? tmpDir)
    {
        var cleanupErrors = new List<Exception>();

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
    /// A streaming chat client that yields a <c>TextDelta</c> then a <c>Completed</c> update,
    /// with an optional delay and cancellation check.
    /// </summary>
    private sealed class StreamingTextClient(string replyText, int delayMs = 0) : IChatClient
    {
        public ChatClientMetadata Metadata => new("stub", null, "stub-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, replyText))
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
            if (delayMs > 0)
                await Task.Delay(delayMs, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                yield break;
            yield return new ChatResponseUpdate(ChatRole.Assistant, replyText)
            {
                FinishReason = ChatFinishReason.Stop,
            };
        }

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    /// <summary>
    /// A streaming chat client that blocks on a <see cref="SemaphoreSlim"/> until released,
    /// allowing tests to keep the streaming loop alive for cancellation/disposal tests.
    /// </summary>
    private sealed class BlockingStreamingClient : IChatClient
    {
        private readonly SemaphoreSlim _releaseSignal = new(0, 1);
        public bool WasCancelled { get; private set; }

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
            // Block until the test releases us or cancellation fires.
            try
            {
                await _releaseSignal.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done")
            {
                FinishReason = ChatFinishReason.Stop,
            };
        }

        public void Release() => _releaseSignal.Release();

        public void Dispose() => _releaseSignal.Dispose();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    // ── 1. SendMessage when not connected throws ──

    [Fact]
    public async Task SendMessage_WhenNotConnected_ThrowsInvalidOperationException()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        var tmpDir = CreateTempDir();
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);
            var streamingService = GetStreamingService(composer);

            var ex = Assert.Throws<InvalidOperationException>(
                () => streamingService.SendMessage("hello"));
            Assert.Contains("not connected", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 2. SendMessage while already streaming throws ──

    [Fact]
    public async Task SendMessage_WhileAlreadyStreaming_ThrowsInvalidOperationException()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        var tmpDir = CreateTempDir();
        BlockingStreamingClient? blockingClient = null;
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);
            blockingClient = new BlockingStreamingClient();
            await InjectFakeChatClient(composer, blockingClient);

            var streamingService = GetStreamingService(composer);

            // Start a stream — it will block until we release the semaphore.
            streamingService.SendMessage("first");
            Assert.True(streamingService.IsStreaming, "Should be streaming after first SendMessage");

            try
            {
                // Attempting a second SendMessage while the first is still active must throw.
                var ex = Assert.Throws<InvalidOperationException>(
                    () => streamingService.SendMessage("second"));
                Assert.Contains("already streaming", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                // Release the blocking client and cancel to allow cleanup.
                streamingService.CancelStreaming();
                blockingClient.Release();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (streamingService.IsStreaming && DateTime.UtcNow < deadline)
                    await Task.Delay(20, CancellationToken.None);
            }
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 3. CancelStreaming cancels the active stream ──

    [Fact]
    public async Task CancelStreaming_CancelsActiveStream_SetsIsStreamingFalse()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        var tmpDir = CreateTempDir();
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);
            var blockingClient = new BlockingStreamingClient();
            await InjectFakeChatClient(composer, blockingClient);

            var streamingService = GetStreamingService(composer);
            streamingService.SendMessage("hello");
            Assert.True(streamingService.IsStreaming);

            // Cancel — the blocking client should observe cancellation.
            streamingService.CancelStreaming();

            // Wait for IsStreaming to become false.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (streamingService.IsStreaming && DateTime.UtcNow < deadline)
                await Task.Delay(20, CancellationToken.None);

            Assert.False(streamingService.IsStreaming, "Streaming should have stopped after cancellation");
            Assert.True(blockingClient.WasCancelled, "The streaming client should have observed cancellation");
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 4. DisposeAsync cancels pending stream and awaits completion ──

    [Fact]
    public async Task DisposeAsync_CancelsPendingStream_AndAwaitsCompletion()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        var tmpDir = CreateTempDir();
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);
            var blockingClient = new BlockingStreamingClient();
            await InjectFakeChatClient(composer, blockingClient);

            var streamingService = GetStreamingService(composer);
            streamingService.SendMessage("hello");
            Assert.True(streamingService.IsStreaming);

            // DisposeAsync should cancel the CTS and await the streaming task.
            // Give a short timeout so we don't hang forever if something is wrong.
            await streamingService.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.False(streamingService.IsStreaming, "Streaming should be stopped after DisposeAsync");
            Assert.True(blockingClient.WasCancelled, "The streaming client should have been cancelled by DisposeAsync");
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 5a. RunStreamingAsync context overflow recovery ──

    [Fact]
    public async Task RunStreamingAsync_ContextOverflow_ResetsSessionAndInvokesOverflowRecoveryCallback()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        var tmpDir = CreateTempDir();
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);

            // Write a fake session file so we can verify the overflow callback deletes it.
            var sessionFile = Path.Combine(tmpDir, "composer-session.json");
            await File.WriteAllTextAsync(sessionFile, "{}", TestContext.Current.CancellationToken);
            Assert.True(File.Exists(sessionFile));

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

            await InjectFakeChatClient(composer, mockClient.Object);

            var streamingService = GetStreamingService(composer);

            // Track whether the OnStreamingUpdate event fires (Composer raises it via callback).
            var updateCount = 0;
            composer.OnStreamingUpdate += () => Interlocked.Increment(ref updateCount);

            streamingService.SendMessage("hello");

            // Wait for streaming to finish.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (streamingService.IsStreaming && DateTime.UtcNow < deadline)
                await Task.Delay(20, CancellationToken.None);

            Assert.False(streamingService.IsStreaming, "Streaming should have finished after overflow");

            // The overflow recovery callback should have reset IsCompacting/WasCompacted and deleted the session file.
            Assert.False(File.Exists(sessionFile), "Session file should be deleted by overflow recovery callback");
            Assert.False(composer.IsCompacting, "IsCompacting should be reset by overflow recovery callback");
            Assert.False(composer.WasCompacted, "WasCompacted should be reset by overflow recovery callback");

            // Streaming content should contain the warning message.
            Assert.Contains("⚠️", streamingService.StreamingContent);
            Assert.Contains("Context limit reached", streamingService.StreamingContent);

            // OnStreamingUpdate should have fired (at least once for the finally block).
            Assert.True(updateCount > 0, "OnStreamingUpdate should have been invoked");
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    [Fact]
    public async Task RunStreamingAsync_ContextOverflow_DoesNotClearComposerAttachments()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        var tmpDir = CreateTempDir();
        try
        {
            var hiveConfig = new HiveConfigFile
            {
                Models = new ModelsConfig
                {
                    AvailableModels =
                    [
                        new ModelEntry { Name = "gpt-4" },
                    ]
                }
            };

            var repoManager = new Mock<IBrainRepoManager>();
            repoManager.SetupGet(r => r.WorkDirectory).Returns(tmpDir);

            dbContext = CopilotHiveDbContext.CreateInMemory();
            var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);

            var attachmentService = new CopilotHive.Services.ComposerAttachmentService(
                tmpDir,
                NullLogger<CopilotHive.Services.ComposerAttachmentService>.Instance);

            var saveResult = await attachmentService.SaveAsync(
                "diagram.png",
                new MemoryStream(new byte[] { 0x01, 0x02, 0x03 }),
                TestContext.Current.CancellationToken);
            Assert.True(saveResult.Success);

            var mockConnectClient = new Mock<IChatClient>();
            composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                store,
                repoManager: repoManager.Object,
                stateDir: tmpDir,
                hiveConfig: hiveConfig,
                chatClientFactory: _ => mockConnectClient.Object,
                attachmentService: attachmentService);

            await composer.ConnectAsync(TestContext.Current.CancellationToken);

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

            await InjectFakeChatClient(composer, mockClient.Object);

            var streamingService = GetStreamingService(composer);
            streamingService.SendMessage("hello");

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (streamingService.IsStreaming && DateTime.UtcNow < deadline)
                await Task.Delay(20, CancellationToken.None);

            Assert.False(streamingService.IsStreaming, "Streaming should have finished after overflow");

            // Overflow recovery resets the agent session but must NOT clear the Composer's attachments.
            var remainingFiles = Directory.GetFiles(attachmentService.AttachmentsRootPath);
            Assert.Single(remainingFiles);
            Assert.Contains(saveResult.Attachment!.SavedRelativePath, remainingFiles[0]);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 5b. RunStreamingAsync generic exception appends error message ──

    [Fact]
    public async Task RunStreamingAsync_GenericException_AppendsErrorMessage()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        var tmpDir = CreateTempDir();
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);

            var genericEx = new InvalidOperationException("Something went wrong (NOT an overflow)");
            var mockClient = new Mock<IChatClient>();
            mockClient
                .Setup(c => c.GetStreamingResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .Throws(genericEx);
            mockClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(genericEx);

            await InjectFakeChatClient(composer, mockClient.Object);

            var streamingService = GetStreamingService(composer);
            streamingService.SendMessage("hello");

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (streamingService.IsStreaming && DateTime.UtcNow < deadline)
                await Task.Delay(20, CancellationToken.None);

            Assert.False(streamingService.IsStreaming, "Streaming should have finished after error");

            // The generic catch block appends the error message to StreamingContent.
            Assert.Contains("❌", streamingService.StreamingContent);
            Assert.Contains("Something went wrong", streamingService.StreamingContent);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 5c. RunStreamingAsync cancellation via CancelStreaming ──

    [Fact]
    public async Task RunStreamingAsync_Cancellation_LogsAndCompletesCleanly()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        var tmpDir = CreateTempDir();
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);
            var blockingClient = new BlockingStreamingClient();
            await InjectFakeChatClient(composer, blockingClient);

            var streamingService = GetStreamingService(composer);
            streamingService.SendMessage("hello");
            Assert.True(streamingService.IsStreaming);

            // Cancel the stream.
            streamingService.CancelStreaming();

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (streamingService.IsStreaming && DateTime.UtcNow < deadline)
                await Task.Delay(20, CancellationToken.None);

            Assert.False(streamingService.IsStreaming, "Streaming should have stopped after cancellation");

            // The OperationCanceledException catch block should NOT append error text.
            // StreamingContent should be empty (no text deltas were yielded before cancellation).
            Assert.DoesNotContain("❌", streamingService.StreamingContent);
            Assert.DoesNotContain("⚠️", streamingService.StreamingContent);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 6a. Streaming loop invokes refreshRegistry callback (streaming → idle) ──

    [Fact]
    public async Task RunStreamingAsync_InvokesRefreshRegistryCallback_StreamingThenIdle()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        var tmpDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            (composer, dbContext) = CreateComposer(tmpDir, sessionRegistry: registry);

            // A blocking client keeps the stream alive so the "streaming" status can be observed
            // before the finally block flips it back to "idle".
            var blockingClient = new BlockingStreamingClient();
            await InjectFakeChatClient(composer, blockingClient);

            var streamingService = GetStreamingService(composer);

            var finished = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            composer.OnStreamingUpdate += () =>
            {
                if (!streamingService.IsStreaming)
                    finished.TrySetResult(true);
            };

            streamingService.SendMessage("hello");

            // Observe the "streaming" transition while the stream is still active.
            var statuses = new List<string>();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var current = registry.GetAll().FirstOrDefault(s => s.SessionId == "composer");
                if (current is not null && current.Status == "streaming")
                {
                    statuses.Add(current.Status);
                    break;
                }
                await Task.Delay(20, CancellationToken.None);
            }

            Assert.Contains("streaming", statuses);

            // Release the stream so the finally block runs and reports "idle".
            blockingClient.Release();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.False(streamingService.IsStreaming);

            var composerSession = Assert.Single(registry.GetAll(), s => s.SessionId == "composer");
            statuses.Add(composerSession.Status);

            // Both transitions must be observed, in order.
            Assert.Equal(["streaming", "idle"], statuses);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 6b. Streaming loop invokes onStreamingUpdate callback ──

    [Fact]
    public async Task RunStreamingAsync_InvokesOnStreamingUpdateCallback()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        var tmpDir = CreateTempDir();
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);

            var client = new StreamingTextClient("Hello world");
            await InjectFakeChatClient(composer, client);

            var streamingService = GetStreamingService(composer);

            var updateCount = 0;
            var finished = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            composer.OnStreamingUpdate += () =>
            {
                Interlocked.Increment(ref updateCount);
                if (!streamingService.IsStreaming)
                    finished.TrySetResult(true);
            };

            streamingService.SendMessage("hello");

            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.False(streamingService.IsStreaming);

            // onStreamingUpdate should fire at least once (for the text delta) and again in the finally block.
            Assert.True(updateCount >= 2,
                $"OnStreamingUpdate should fire at least twice (delta + finally), got {updateCount}");

            // StreamingContent should contain the streamed text.
            Assert.Contains("Hello world", streamingService.StreamingContent);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 6c. Streaming loop invokes saveSession callback ──

    [Fact]
    public async Task RunStreamingAsync_InvokesSaveSessionCallback()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        var tmpDir = CreateTempDir();
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);

            var client = new StreamingTextClient("Save me");
            await InjectFakeChatClient(composer, client);

            var streamingService = GetStreamingService(composer);

            var finished = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            composer.OnStreamingUpdate += () =>
            {
                if (!streamingService.IsStreaming)
                    finished.TrySetResult(true);
            };

            streamingService.SendMessage("hello");

            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.False(streamingService.IsStreaming);

            // The saveSession callback should have persisted the session file.
            var sessionFile = Path.Combine(tmpDir, "composer-session.json");
            Assert.True(File.Exists(sessionFile),
                "Session file should exist — saveSession callback was invoked");
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 6d. Streaming loop resets LastToolCalls to 0 at start ──

    [Fact]
    public async Task RunStreamingAsync_ResetsLastToolCalls_ToZeroAtStart()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        var tmpDir = CreateTempDir();
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);

            var client = new StreamingTextClient("Hello world");
            await InjectFakeChatClient(composer, client);

            var streamingService = GetStreamingService(composer);

            var finished = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            composer.OnStreamingUpdate += () =>
            {
                if (!streamingService.IsStreaming)
                    finished.TrySetResult(true);
            };

            streamingService.SendMessage("hello");

            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.False(streamingService.IsStreaming);

            // LastToolCalls starts at 0 and remains 0 when the agent doesn't report tool calls.
            Assert.Equal(0, streamingService.LastToolCalls);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 7. DisposeAsync when no stream is active does not throw ──

    [Fact]
    public async Task DisposeAsync_WhenNoStreamActive_DoesNotThrow()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        var tmpDir = CreateTempDir();
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);
            var streamingService = GetStreamingService(composer);

            // No SendMessage called — _streamingTask is null, _streamCts is null.
            // DisposeAsync should complete without throwing.
            await streamingService.DisposeAsync();
            Assert.False(streamingService.IsStreaming);
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 8. IsContextOverflowError static helper ──

    [Fact]
    public void IsContextOverflowError_DetectsOverflowInInnerException()
    {
        var inner = new InvalidOperationException("model_max_prompt_tokens_exceeded");
        var outer = new ApplicationException("Wrapper", inner);

        Assert.True(ComposerStreamingService.IsContextOverflowError(outer));
        Assert.True(ComposerStreamingService.IsContextOverflowError(inner));
        Assert.False(ComposerStreamingService.IsContextOverflowError(
            new InvalidOperationException("unrelated error")));
        Assert.False(ComposerStreamingService.IsContextOverflowError(null));
    }

    // ── 9. IsContextOverflowError delegates from Composer ──

    [Fact]
    public void Composer_IsContextOverflowError_DelegatesToStreamingService()
    {
        var ex = new InvalidOperationException("model_max_prompt_tokens_exceeded");
        Assert.True(Composer.IsContextOverflowError(ex));

        // Verify it's the same as the streaming service's implementation.
        Assert.Equal(
            ComposerStreamingService.IsContextOverflowError(ex),
            Composer.IsContextOverflowError(ex));
    }

    // ── 10. Composer.DisposeAsync exception-safe disposal ──

    [Fact]
    public async Task Composer_DisposeAsync_StreamingServiceThrows_AgentServiceStillDisposed()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        var tmpDir = CreateTempDir();
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);

            // Inject a chat client so the agent service is "connected".
            var client = new StreamingTextClient("hello");
            await InjectFakeChatClient(composer, client);
            Assert.True(composer.IsConnected, "Composer should be connected after injecting chat client");

            // Get the streaming service and its internal _streamCts.
            var streamingService = GetStreamingService(composer);
            var ctsField = typeof(ComposerStreamingService).GetField("_streamCts", PrivateFlags)
                ?? throw new InvalidOperationException("_streamCts field not found");
            var cts = (CancellationTokenSource?)ctsField.GetValue(streamingService);

            // If there's no CTS (no streaming started), create one and set it, then dispose it
            // so that DisposeAsync's cts?.Cancel() throws ObjectDisposedException.
            if (cts is null)
            {
                cts = new CancellationTokenSource();
                ctsField.SetValue(streamingService, cts);
            }
            cts.Dispose(); // Now Cancel() inside DisposeAsync will throw ObjectDisposedException

            // Composer.DisposeAsync swallows and logs the streaming-service disposal failure
            // (try/catch around _streamingService.DisposeAsync) and still disposes the agent
            // service. DisposeAsync must complete WITHOUT throwing.
            await composer.DisposeAsync();

            // The agent service should have been disposed despite the streaming service throwing.
            Assert.False(composer.IsConnected, "Agent service should be disposed even when streaming service disposal throws");

            // Already disposed — skip the outer cleanup's double-dispose.
            composer = null;
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 11. ComposerStreamingService.DisposeAsync with a faulted streaming task ──

    [Fact]
    public async Task DisposeAsync_FaultedStreamingTask_CtsStillDisposedAndDoesNotThrow()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        var tmpDir = CreateTempDir();
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);

            // Connect the agent so the service is in a realistic state.
            await InjectFakeChatClient(composer, new StreamingTextClient("hello"));

            var streamingService = GetStreamingService(composer);

            var ctsField = typeof(ComposerStreamingService).GetField("_streamCts", PrivateFlags)
                ?? throw new InvalidOperationException("_streamCts field not found");
            var taskField = typeof(ComposerStreamingService).GetField("_streamingTask", PrivateFlags)
                ?? throw new InvalidOperationException("_streamingTask field not found");

            // Inject a live CTS and a task that faulted with a NON-cancellation exception.
            var cts = new CancellationTokenSource();
            ctsField.SetValue(streamingService, cts);
            taskField.SetValue(streamingService,
                Task.FromException(new InvalidOperationException("test fault")));

            // DisposeAsync must swallow the non-cancellation fault (logged as a warning).
            await streamingService.DisposeAsync();

            // The owned CTS reference must have been cleared…
            Assert.Null(ctsField.GetValue(streamingService));

            // …and the captured CTS must have actually been disposed by the finally block.
            // This assertion fails if `cts?.Dispose()` is removed from DisposeAsync.
            Assert.Throws<ObjectDisposedException>(() => cts.Token.Register(() => { }));
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 12. saveSession callback is awaited (not fire-and-forget) ──

    [Fact]
    public async Task RunStreamingAsync_SaveSessionCallback_IsAwaited_BlocksUntilCompleted()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        var tmpDir = CreateTempDir();
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);

            var client = new StreamingTextClient("streamed text");
            await InjectFakeChatClient(composer, client);

            var streamingService = GetStreamingService(composer);

            // Replace the saveSession callback with a TaskCompletionSource-backed one.
            // The streaming loop should block on `await saveSession(ct)` until the TCS is completed.
            var saveSessionTcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            // Get the delegate field for saveSession from the streaming service's primary constructor.
            // ComposerStreamingService uses a primary constructor, so the parameter is a field.
            // The compiler-generated backing field name for primary constructor parameters is the parameter name itself.
            var saveSessionField = typeof(ComposerStreamingService)
                .GetField("saveSession", PrivateFlags)
                ?? typeof(ComposerStreamingService).GetField("<saveSession>", PrivateFlags)
                ?? typeof(ComposerStreamingService).GetField("<saveSession>P", PrivateFlags)
                ?? typeof(ComposerStreamingService)
                    .GetFields(PrivateFlags)
                    .FirstOrDefault(f => f.Name.Contains("saveSession", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    "saveSession field not found on ComposerStreamingService. Fields: " +
                    string.Join(", ", typeof(ComposerStreamingService).GetFields(PrivateFlags).Select(f => f.Name)));

            // Replace the saveSession callback with one that waits on the TCS.
            Func<CancellationToken, Task> newSaveSession = ct => saveSessionTcs.Task;
            saveSessionField.SetValue(streamingService, newSaveSession);

            // Track streaming completion via OnStreamingUpdate.
            var finished = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            composer.OnStreamingUpdate += () =>
            {
                if (!streamingService.IsStreaming)
                    finished.TrySetResult(true);
            };

            // Start streaming — the streaming loop will complete the await foreach, then
            // hit `await saveSession(ct)` which blocks on our TCS.
            streamingService.SendMessage("hello");

            // Give the streaming loop time to process the text delta and reach the saveSession await.
            await Task.Delay(500, TestContext.Current.CancellationToken);

            // The streaming loop should still be "streaming" because saveSession hasn't completed yet.
            Assert.True(streamingService.IsStreaming,
                "Streaming should still be active while saveSession is pending (awaited, not fire-and-forget)");

            // The OnStreamingUpdate event should NOT have signalled completion yet.
            Assert.False(finished.Task.IsCompleted,
                "Streaming should not have finished while saveSession is still pending");

            // Now complete the saveSession TCS — the streaming loop should proceed to the finally block.
            saveSessionTcs.SetResult(true);

            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(streamingService.IsStreaming,
                "Streaming should have finished after saveSession completed");
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 13. Cancellation failure bypasses await — CTS.Cancel throws but streaming task is still awaited ──

    [Fact]
    public async Task DisposeAsync_CtsCancelThrows_StreamingTaskStillAwaited_CtsStillDisposed()
    {
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        var tmpDir = CreateTempDir();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);
            await InjectFakeChatClient(composer, new StreamingTextClient("hello"));

            var streamingService = GetStreamingService(composer);

            var ctsField = typeof(ComposerStreamingService).GetField("_streamCts", PrivateFlags)
                ?? throw new InvalidOperationException("_streamCts field not found");
            var taskField = typeof(ComposerStreamingService).GetField("_streamingTask", PrivateFlags)
                ?? throw new InvalidOperationException("_streamingTask field not found");

            // Pre-dispose the CTS so Cancel() throws ObjectDisposedException.
            var cts = new CancellationTokenSource();
            cts.Dispose();
            ctsField.SetValue(streamingService, cts);

            // Inject an INCOMPLETE task so we can observe that DisposeAsync really awaits it.
            taskField.SetValue(streamingService, tcs.Task);

            try
            {
                var disposeTask = streamingService.DisposeAsync().AsTask();

                // Cancel() already threw; if the `await task` were skipped, disposeTask
                // would have completed immediately. It must still be blocked.
                await Task.WhenAny(disposeTask, Task.Delay(200, TestContext.Current.CancellationToken));
                Assert.False(disposeTask.IsCompleted,
                    "DisposeAsync should be blocked awaiting the streaming task");

                // Unblock the streaming task.
                tcs.SetResult(true);

                var thrown = await Assert.ThrowsAnyAsync<Exception>(
                    () => disposeTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

                // The captured cancellation failure is rethrown after cleanup completes.
                Assert.IsType<ObjectDisposedException>(thrown);

                // The owned CTS reference must have been cleared and the CTS disposed.
                Assert.Null(ctsField.GetValue(streamingService));
                Assert.Throws<ObjectDisposedException>(() => cts.Token.Register(() => { }));
            }
            finally
            {
                // Never let a failed assertion leave the injected task hanging.
                tcs.TrySetResult(true);
            }
        }
        finally
        {
            await CleanupAsync(composer, dbContext, tmpDir);
        }
    }

    // ── 14. Cleanup helper throws on failure (does not silently swallow) ──

    [Fact]
    public async Task CleanupAsync_WhenComposerDisposalThrows_StillCleansOtherResources()
    {
        var tmpDir = CreateTempDir();
        Composer? composer = null;
        CopilotHiveDbContext? dbContext = null;
        try
        {
            (composer, dbContext) = CreateComposer(tmpDir);

            // Inject a chat client whose disposal throws, so _agentService.DisposeAsync()
            // (NOT wrapped in try/catch by the new Composer.DisposeAsync) propagates the failure.
            var throwingClient = new ThrowingDisposeChatClient();
            await InjectFakeChatClient(composer, throwingClient);

            // CleanupAsync must surface the disposal failure rather than swallowing it…
            var ex = await Assert.ThrowsAsync<AggregateException>(
                () => CleanupAsync(composer, dbContext, tmpDir));

            Assert.NotEmpty(ex.InnerExceptions);
            Assert.Contains(ex.InnerExceptions,
                e => e is InvalidOperationException or AggregateException);

            // …while still releasing the remaining resources.
            Assert.False(Directory.Exists(tmpDir),
                "Temp directory should still be deleted even when Composer disposal throws");

            // Already cleaned up by CleanupAsync — skip the outer finally.
            composer = null;
            dbContext = null;
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

    /// <summary>Chat client whose disposal throws, exercising the agent-service disposal failure path.</summary>
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
            => throw new NotSupportedException("Streaming not used in this stub.");

        public void Dispose() => throw new InvalidOperationException("client dispose boom");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
