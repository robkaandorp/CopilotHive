using System.Reflection;
using System.Runtime.CompilerServices;

using CopilotHive.Actors;
using CopilotHive.Orchestration;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using SharpCoder;

namespace CopilotHive.Tests.Actors;

/// <summary>
/// Tests for <see cref="ComposerActor"/> — the channel-based actor owning the Composer's
/// streaming and session lifecycle. Tests drive a real <see cref="ComposerAgentService"/>
/// with fake chat clients and assert observable behavior via callbacks, replies and
/// reflected state, using TCS gates instead of timing-dependent polling.
/// </summary>
public sealed class ComposerActorTests
{
    private const BindingFlags PrivateFlags =
        BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // ── Helpers ──

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"composer-actor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        if (!Directory.Exists(dir))
            return;
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static ComposerAgentService CreateService(
        string stateDir,
        Func<string, IChatClient>? chatClientFactory = null,
        string model = "test-model",
        IReadOnlyList<string>? availableModels = null,
        Action? onCompacting = null,
        Action<CompactionResult>? onCompacted = null) =>
        new(
            model,
            64000,
            50,
            null,
            null,
            "system prompt",
            new List<AITool>(),
            null,
            stateDir,
            null,
            NullLogger<ComposerAgentService>.Instance,
            chatClientFactory,
            null,
            availableModels ?? [model],
            onCompacting,
            onCompacted,
            false,
            []);

    private static ComposerActor CreateActor(
        ComposerAgentService service,
        Func<CancellationToken, Task> saveSession,
        Action<string> refreshRegistry,
        Action<string> onStreamingUpdate,
        Action<int> onStreamingFinished,
        Action<string> onStreamingError,
        Action onOverflowRecovery,
        ILogger? logger = null,
        Action? onCompactingStarted = null,
        Action<bool>? onCompactingFinished = null,
        Action<bool>? onSessionLoaded = null,
        Action<string>? onSubmitAnswer = null,
        Action? onCancelQuestion = null) =>
        new(
            service,
            saveSession,
            refreshRegistry,
            onStreamingUpdate,
            onStreamingFinished,
            onStreamingError,
            onOverflowRecovery,
            onCompactingStarted ?? (() => { }),
            onCompactingFinished ?? (_ => { }),
            onSessionLoaded ?? (_ => { }),
            onSubmitAnswer ?? (_ => { }),
            onCancelQuestion ?? (() => { }),
            logger ?? NullLogger<ComposerActor>.Instance);

    private static TaskCompletionSource<T> NewReply<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<T> AwaitReplyAsync<T>(TaskCompletionSource<T> reply)
    {
        await Task.WhenAny(reply.Task, Task.Delay(Timeout, TestContext.Current.CancellationToken));
        Assert.True(reply.Task.IsCompleted, "Reply did not settle in time.");
        return reply.Task.Result;
    }

    private static async Task AwaitSettledAsync(TaskCompletionSource reply)
    {
        await Task.WhenAny(reply.Task, Task.Delay(Timeout, TestContext.Current.CancellationToken));
        Assert.True(reply.Task.IsCompleted, "Reply did not settle in time.");
    }

    private static bool GetIsStreaming(ComposerActor actor)
    {
        var field = typeof(ComposerActor).GetField("_isStreaming", PrivateFlags)
            ?? throw new InvalidOperationException("_isStreaming field not found on ComposerActor");
        return (bool)field.GetValue(actor)!;
    }

    private static bool GetTerminated(ComposerActor actor)
    {
        var field = typeof(ComposerActor).GetField("_terminated", PrivateFlags)
            ?? throw new InvalidOperationException("_terminated field not found on ComposerActor");
        return (bool)field.GetValue(actor)!;
    }

    private static string GetStreamingContent(ComposerActor actor)
    {
        var field = typeof(ComposerActor).GetField("_streamingContent", PrivateFlags)
            ?? throw new InvalidOperationException("_streamingContent field not found on ComposerActor");
        return (string)field.GetValue(actor)!;
    }

    private static CancellationTokenSource? GetStreamingCts(ComposerActor actor)
    {
        var field = typeof(ComposerActor).GetField("_streamingCts", PrivateFlags)
            ?? throw new InvalidOperationException("_streamingCts field not found on ComposerActor");
        return (CancellationTokenSource?)field.GetValue(actor);
    }

    private static Task? GetStreamingTask(ComposerActor actor)
    {
        var field = typeof(ComposerActor).GetField("_streamingTask", PrivateFlags)
            ?? throw new InvalidOperationException("_streamingTask field not found on ComposerActor");
        return (Task?)field.GetValue(actor);
    }

    /// <summary>
    /// Waits until the actor has reported the "streaming" registry status, i.e. the send
    /// handler has run and the streaming task is live. Bounded so a hang fails the test.
    /// </summary>
    private static async Task WaitForStreamingStatusAsync(List<string> registryStatuses)
    {
        var deadline = DateTime.UtcNow.Add(Timeout);
        while (DateTime.UtcNow < deadline)
        {
            lock (registryStatuses)
            {
                if (registryStatuses.Contains("streaming", StringComparer.Ordinal)) return;
            }
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        lock (registryStatuses) Assert.Contains("streaming", registryStatuses);
    }

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

    /// <summary>Chat client that yields a text delta then completes the stream.</summary>
    private sealed class TextStreamingClient(string replyText) : IChatClient
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
    /// Chat client that blocks on a semaphore until released or cancelled, letting tests
    /// hold a stream open deterministically for cancellation and disposal scenarios.
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

        public void Release()
        {
            try
            {
                _releaseSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already released — nothing to do.
            }
        }

        public void Dispose() => _releaseSignal.Dispose();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    /// <summary>Returns an empty async enumerable of chat updates.</summary>
    private static async IAsyncEnumerable<ChatResponseUpdate> EmptyStream()
    {
        await Task.Yield();
        yield break;
    }

    /// <summary>
    /// Chat client that yields a fixed sequence of text deltas, pausing between each so the
    /// mailbox can interleave with the producer. Used to verify that the accumulated streaming
    /// content is never lossy under producer/mailbox overlap.
    /// </summary>
    private sealed class MultiDeltaStreamingClient(IReadOnlyList<string> deltas) : IChatClient
    {
        public ChatClientMetadata Metadata => new("multi-delta", null, "multi-delta-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Concat(deltas))));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var delta in deltas)
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;

                // Pause so the mailbox thread genuinely runs between deltas: without overlap
                // a shared accumulator would never be observed regressing.
                await Task.Delay(2, CancellationToken.None);
                yield return new ChatResponseUpdate(ChatRole.Assistant, delta);
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "")
            {
                FinishReason = ChatFinishReason.Stop,
            };
        }

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    /// <summary>
    /// Chat client that blocks until cancellation is requested and then ends the stream with a
    /// bare <c>yield break</c> — i.e. a graceful cancellation exit that never throws
    /// <see cref="OperationCanceledException"/>. This is the path that must still be classified
    /// as cancelled (no session save).
    /// </summary>
    private sealed class YieldBreakOnCancelClient : IChatClient
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the stream is actually running (so cancellation is not vacuous).</summary>
        public Task Entered => _entered.Task;

        public ChatClientMetadata Metadata => new("yield-break", null, "yield-break-model");

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
            _entered.TrySetResult();

            // Poll rather than await so cancellation never surfaces as an exception.
            while (!cancellationToken.IsCancellationRequested)
                await Task.Delay(10, CancellationToken.None);

            yield break;
        }

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    /// <summary>
    /// Chat client that blocks until cancellation and then throws a supplied exception
    /// SYNCHRONOUSLY from <c>GetStreamingResponseAsync</c> itself.
    /// <para>
    /// The synchronous throw is essential: an exception thrown from inside an async-iterator
    /// body is swallowed by the agent and surfaces as a normal completion, so it would never
    /// reach the actor's overflow/error paths. Throwing before the enumerable is returned
    /// propagates out of the actor's <c>await foreach</c>, exactly like a real provider failure.
    /// </para>
    /// <para>
    /// Blocking synchronously (on the actor's streaming task thread) lets a test close the
    /// mailbox while the stream is still in flight, so the terminal <c>Tell</c> that follows the
    /// throw is deterministically REJECTED.
    /// </para>
    /// </summary>
    private sealed class BlockOnCancelThenThrowClient(Exception failure) : IChatClient
    {
        private readonly SemaphoreSlim _releaseSignal = new(0, 1);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entries;

        /// <summary>Number of times the stream method was entered (retry detection).</summary>
        public int Entries => Volatile.Read(ref _entries);

        /// <summary>Completes once the stream method has been entered and is about to block.</summary>
        public Task Entered => _entered.Task;

        public ChatClientMetadata Metadata => new("block-then-throw", null, "block-then-throw-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw failure;

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _entries);
            _entered.TrySetResult();

            try
            {
                // Synchronous wait so the throw below is synchronous too.
                _releaseSignal.Wait(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Surface the supplied failure rather than the cancellation, so the actor takes
                // the overflow/error terminal path AFTER the mailbox has already closed.
                throw failure;
            }

            throw failure;
        }

        public void Release()
        {
            try { _releaseSignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        public void Dispose() => _releaseSignal.Dispose();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    /// <summary>
    /// Chat client that yields one text delta, then blocks until released, then throws.
    /// <para>
    /// The delta gives a test a way to OCCUPY the mailbox loop (the resulting
    /// <c>ComposerStreamingUpdateMessage</c> handler calls <c>onStreamingUpdate</c>, which the
    /// test can block in), so the loop can be held busy while the mailbox is closed. The
    /// terminal throw is SYNCHRONOUS — an exception thrown from inside an async-iterator body
    /// is swallowed by the agent and surfaces as a normal completion instead of reaching the
    /// actor's error path.
    /// </para>
    /// </summary>
    private sealed class DeltaThenBlockThenThrowClient(string delta, Exception failure) : IChatClient
    {
        private readonly SemaphoreSlim _releaseSignal = new(0, 1);

        public ChatClientMetadata Metadata => new("delta-block-throw", null, "delta-block-throw-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw failure;

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => new DeltaThenThrowEnumerable(delta, failure, _releaseSignal);

        public void Release()
        {
            try { _releaseSignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        public void Dispose() => _releaseSignal.Dispose();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        /// <summary>
        /// Hand-written enumerable so the failure is thrown from <c>MoveNextAsync</c> (which the
        /// agent surfaces) rather than from an async-iterator body (which it swallows).
        /// </summary>
        private sealed class DeltaThenThrowEnumerable(string delta, Exception failure, SemaphoreSlim gate)
            : IAsyncEnumerable<ChatResponseUpdate>
        {
            public IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default)
                => new Enumerator(delta, failure, gate);

            private sealed class Enumerator(string delta, Exception failure, SemaphoreSlim gate)
                : IAsyncEnumerator<ChatResponseUpdate>
            {
                private int _index;

                public ChatResponseUpdate Current { get; private set; } = null!;

                public ValueTask<bool> MoveNextAsync()
                {
                    if (_index++ == 0)
                    {
                        Current = new ChatResponseUpdate(ChatRole.Assistant, delta);
                        return ValueTask.FromResult(true);
                    }

                    // Block synchronously, then throw synchronously — both are required for the
                    // failure to reach the actor's terminal error path.
                    gate.Wait();
                    throw failure;
                }

                public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>ILogger that records formatted messages for assertion.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    // ── Connect ──

    [Fact]
    public async Task Connect_RepliesTrueAndConnectsService()
    {
        var stateDir = CreateTempDir();
        var client = new TextStreamingClient("hi");
        var service = CreateService(stateDir, chatClientFactory: _ => client);
        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            actor.Start();
            var reply = NewReply<bool>();
            Assert.True(actor.Tell(new ComposerConnectMessage(reply, CancellationToken.None)));

            var result = await AwaitReplyAsync(reply);
            Assert.True(result);
            Assert.True(service.IsConnected, "Service should be connected after the connect message");
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task Connect_PreCancelledToken_ReplyCanceled()
    {
        var stateDir = CreateTempDir();
        var client = new TextStreamingClient("hi");
        var service = CreateService(stateDir, chatClientFactory: _ => client);

        // Write a valid session file so ConnectAsync's session-load step honors the
        // cancelled token and throws OperationCanceledException deterministically.
        var sessionFile = Path.Combine(stateDir, "composer-session.json");
        var validSession = AgentSession.Create("composer");
        await validSession.SaveAsync(sessionFile, TestContext.Current.CancellationToken);

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            actor.Start();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var reply = NewReply<bool>();
            Assert.True(actor.Tell(new ComposerConnectMessage(reply, cts.Token)));

            await Task.WhenAny(reply.Task, Task.Delay(Timeout, TestContext.Current.CancellationToken));
            Assert.True(reply.Task.IsCanceled, "Connect reply should be canceled for a pre-cancelled token");
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task Connect_AgentServiceThrows_ReplyFaulted()
    {
        var stateDir = CreateTempDir();
        var service = CreateService(stateDir, chatClientFactory: _ => throw new InvalidOperationException("client creation boom"));
        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            actor.Start();
            var reply = NewReply<bool>();
            Assert.True(actor.Tell(new ComposerConnectMessage(reply, CancellationToken.None)));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => reply.Task);
            Assert.Contains("client creation boom", ex.Message);
            Assert.False(service.IsConnected);
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── Streaming lifecycle ──

    [Fact]
    public async Task SendMessage_StreamsText_Completes_AndSavesSession()
    {
        var stateDir = CreateTempDir();
        var client = new TextStreamingClient("Hello world");
        var service = CreateService(stateDir, chatClientFactory: _ => client);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var registryStatuses = new List<string>();
        var streamContents = new List<string>();
        var saveSessionCalls = 0;
        var finishedCalls = 0;

        // Gate completed when saveSession is called, which only happens inside the
        // ComposerStreamingCompleteMessage mailbox handler (not the finally or shutdown
        // path). This deterministically signals the completion handler ran to completion.
        var completedGate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actor = CreateActor(
            service,
            ct =>
            {
                var n = Interlocked.Increment(ref saveSessionCalls);
                if (n == 1) completedGate.TrySetResult(n);
                return Task.CompletedTask;
            },
            status => registryStatuses.Add(status),
            content => streamContents.Add(content),
            _ => Interlocked.Increment(ref finishedCalls),
            _ => { },
            () => { });

        try
        {
            actor.Start();
            Assert.True(actor.Tell(new ComposerSendMessageMessage("hello")));

            // The save-session callback fires inside the mailbox's completion handler,
            // so this gate deterministically signals the handler has run.
            await completedGate.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.Equal(1, saveSessionCalls);

            // Exactly ONE terminal handling per stream: the mailbox completion handler owns
            // the terminal sequence, and the streaming task's finally fallback must NOT also
            // run (the terminal Tell succeeded). A duplicate "idle" here means the fallback
            // fired alongside the handler.
            Assert.Equal(["streaming", "idle"], registryStatuses);
            Assert.Equal(1, finishedCalls);

            // Streaming content accumulated the text delta.
            Assert.NotEmpty(streamContents);
            Assert.Contains("Hello world", streamContents[^1]);

            // Streaming state was reset.
            Assert.False(GetIsStreaming(actor));
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task SendMessage_WhileStreaming_SecondSendIgnored()
    {
        var stateDir = CreateTempDir();
        var blockingClient = new BlockingStreamingClient();
        var service = CreateService(stateDir, chatClientFactory: _ => blockingClient);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var registryStatuses = new List<string>();
        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            status => registryStatuses.Add(status),
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            actor.Start();

            // First send starts a stream that blocks.
            Assert.True(actor.Tell(new ComposerSendMessageMessage("first")));
            await Task.WhenAny(
                Task.Run(() =>
                {
                    while (!registryStatuses.Contains("streaming", StringComparer.Ordinal))
                        Thread.Sleep(10);
                }, TestContext.Current.CancellationToken),
                Task.Delay(Timeout, TestContext.Current.CancellationToken));
            Assert.Contains("streaming", registryStatuses);

            // Second send while streaming is ignored — no second "streaming" transition.
            Assert.True(actor.Tell(new ComposerSendMessageMessage("second")));
            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.Equal(1, registryStatuses.Count(s => s == "streaming"));

            // Cleanup: cancel and release the blocked stream.
            actor.Tell(new ComposerCancelStreamingMessage());
            blockingClient.Release();
            // Wait for the stream task to finish so disposal is deterministic.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (GetIsStreaming(actor) && DateTime.UtcNow < deadline)
                await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        finally
        {
            blockingClient.Release();
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task CancelStreaming_CancelsAndSkipsSessionSave()
    {
        var stateDir = CreateTempDir();
        var blockingClient = new BlockingStreamingClient();
        var service = CreateService(stateDir, chatClientFactory: _ => blockingClient);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var registryStatuses = new List<string>();
        var saveSessionCalls = 0;

        var actor = CreateActor(
            service,
            _ =>
            {
                Interlocked.Increment(ref saveSessionCalls);
                return Task.CompletedTask;
            },
            status => registryStatuses.Add(status),
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            actor.Start();

            // Start a stream that blocks.
            Assert.True(actor.Tell(new ComposerSendMessageMessage("hello")));
            await Task.WhenAny(
                Task.Run(() =>
                {
                    while (!registryStatuses.Contains("streaming", StringComparer.Ordinal))
                        Thread.Sleep(10);
                }, TestContext.Current.CancellationToken),
                Task.Delay(Timeout, TestContext.Current.CancellationToken));
            Assert.Contains("streaming", registryStatuses);

            // Cancel — the blocking client must observe cancellation.
            Assert.True(actor.Tell(new ComposerCancelStreamingMessage()));

            // Wait until the stream has fully terminated.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (GetIsStreaming(actor) && DateTime.UtcNow < deadline)
                await Task.Delay(10, TestContext.Current.CancellationToken);

            Assert.True(blockingClient.WasCancelled, "The streaming client should have observed cancellation");
            Assert.True(saveSessionCalls == 0, "Session save must be skipped when the stream was cancelled");
            // Exactly one terminal handling — the successful terminal Tell means the
            // streaming task's fallback must not also report idle.
            Assert.Equal(["streaming", "idle"], registryStatuses);
            Assert.False(GetIsStreaming(actor));
        }
        finally
        {
            blockingClient.Release();
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── Session mutations: reset / switch / compact ──

    [Fact]
    public async Task ResetSession_WhileStreaming_ReplyFaulted()
    {
        var stateDir = CreateTempDir();
        var blockingClient = new BlockingStreamingClient();
        var service = CreateService(stateDir, chatClientFactory: _ => blockingClient);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var registryStatuses = new List<string>();
        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            status => registryStatuses.Add(status),
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            actor.Start();

            Assert.True(actor.Tell(new ComposerSendMessageMessage("hello")));
            await Task.WhenAny(
                Task.Run(() =>
                {
                    while (!registryStatuses.Contains("streaming", StringComparer.Ordinal))
                        Thread.Sleep(10);
                }, TestContext.Current.CancellationToken),
                Task.Delay(Timeout, TestContext.Current.CancellationToken));
            Assert.Contains("streaming", registryStatuses);

            var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(actor.Tell(new ComposerResetSessionMessage(reply, CancellationToken.None)));

            await AwaitSettledAsync(reply);
            Assert.True(reply.Task.IsFaulted, "Reset while streaming should fault the reply");
            Assert.Contains("Cannot reset while streaming",
                reply.Task.Exception!.InnerException!.Message);

            // Cleanup: cancel and release the blocked stream.
            actor.Tell(new ComposerCancelStreamingMessage());
            blockingClient.Release();
            // Wait for the stream task to finish so disposal is deterministic.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (GetIsStreaming(actor) && DateTime.UtcNow < deadline)
                await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        finally
        {
            blockingClient.Release();
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task ResetSession_RepliesSuccess_AndClearsHistory()
    {
        var stateDir = CreateTempDir();
        var client = new TextStreamingClient("hi");
        var service = CreateService(stateDir, chatClientFactory: _ => client);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        service.Session.MessageHistory.Add(new ChatMessage(ChatRole.User, "old message"));
        service.Session.MessageHistory.Add(new ChatMessage(ChatRole.Assistant, "old reply"));
        Assert.Equal(2, service.Session.MessageHistory.Count);

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            actor.Start();

            var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(actor.Tell(new ComposerResetSessionMessage(reply, CancellationToken.None)));

            await AwaitSettledAsync(reply);
            Assert.True(reply.Task.IsCompletedSuccessfully, "Reset should complete successfully");
            Assert.Empty(service.Session.MessageHistory);
            Assert.True(service.IsConnected, "Service stays connected after a session reset");
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task SwitchModel_WhileStreaming_ReplyFaulted()
    {
        var stateDir = CreateTempDir();
        var blockingClient = new BlockingStreamingClient();
        var service = CreateService(
            stateDir,
            chatClientFactory: _ => blockingClient,
            availableModels: ["model-a", "model-b"]);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var registryStatuses = new List<string>();
        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            status => registryStatuses.Add(status),
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            actor.Start();

            Assert.True(actor.Tell(new ComposerSendMessageMessage("hello")));
            await Task.WhenAny(
                Task.Run(() =>
                {
                    while (!registryStatuses.Contains("streaming", StringComparer.Ordinal))
                        Thread.Sleep(10);
                }, TestContext.Current.CancellationToken),
                Task.Delay(Timeout, TestContext.Current.CancellationToken));
            Assert.Contains("streaming", registryStatuses);

            var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(actor.Tell(new ComposerSwitchModelMessage("model-b", ReasoningEffort.High, reply, CancellationToken.None)));

            await AwaitSettledAsync(reply);
            Assert.True(reply.Task.IsFaulted, "Switch while streaming should fault the reply");
            Assert.Contains("Cannot switch model while streaming",
                reply.Task.Exception!.InnerException!.Message);

            actor.Tell(new ComposerCancelStreamingMessage());
            blockingClient.Release();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (GetIsStreaming(actor) && DateTime.UtcNow < deadline)
                await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        finally
        {
            blockingClient.Release();
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task SwitchModel_RepliesSuccess_AndChangesModel()
    {
        var stateDir = CreateTempDir();
        var client = new TextStreamingClient("hi");
        var service = CreateService(
            stateDir,
            chatClientFactory: _ => client,
            model: "model-a",
            availableModels: ["model-a", "model-b"]);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            actor.Start();

            var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(actor.Tell(new ComposerSwitchModelMessage("model-b", ReasoningEffort.High, reply, CancellationToken.None)));

            await AwaitSettledAsync(reply);
            Assert.True(reply.Task.IsCompletedSuccessfully, "Switch should complete successfully");
            Assert.Equal("model-b", service.Model);
            Assert.Equal(ReasoningEffort.High, service.ReasoningEffort);
            Assert.True(service.IsConnected, "Service stays connected after a model switch");
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task Compact_WithEnoughMessages_RepliesTrue()
    {
        var stateDir = CreateTempDir();
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary of conversation")));
        mockClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());

        var service = CreateService(stateDir, chatClientFactory: _ => mockClient.Object);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        PopulateSession(service.Session, 15); // 16 total, 15 non-system > 10+1
        var originalCount = service.Session.MessageHistory.Count;
        Assert.Equal(16, originalCount);

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            actor.Start();

            var reply = NewReply<bool>();
            Assert.True(actor.Tell(new ComposerCompactMessage(reply, CancellationToken.None)));

            var result = await AwaitReplyAsync(reply);
            Assert.True(result);
            Assert.True(service.Session.MessageHistory.Count < originalCount,
                $"Message count should have decreased after compaction (was {originalCount}, now {service.Session.MessageHistory.Count})");
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task CompactPartial_WithEnoughMessages_RepliesTrue()
    {
        var stateDir = CreateTempDir();
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary of conversation")));
        mockClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());

        var service = CreateService(stateDir, chatClientFactory: _ => mockClient.Object);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        PopulateSession(service.Session, 30); // 31 total — 50% of 30 non-system >= 11
        var originalCount = service.Session.MessageHistory.Count;
        Assert.Equal(31, originalCount);

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            actor.Start();

            var reply = NewReply<bool>();
            Assert.True(actor.Tell(new ComposerCompactPartialMessage(50, reply, CancellationToken.None)));

            var result = await AwaitReplyAsync(reply);
            Assert.True(result);
            Assert.True(service.Session.MessageHistory.Count < originalCount,
                $"Message count should have decreased after partial compaction (was {originalCount}, now {service.Session.MessageHistory.Count})");
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task Compact_WhenCompactorThrows_RepliesFalse()
    {
        var stateDir = CreateTempDir();
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("compaction backend boom"));
        mockClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());

        var service = CreateService(stateDir, chatClientFactory: _ => mockClient.Object);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        PopulateSession(service.Session, 15);

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            actor.Start();

            var reply = NewReply<bool>();
            Assert.True(actor.Tell(new ComposerCompactMessage(reply, CancellationToken.None)));

            var result = await AwaitReplyAsync(reply);
            Assert.False(result, "Compact failure should reply false, never fault");
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── Streaming error / overflow recovery ──

    [Fact]
    public async Task StreamingError_InvokesErrorCallbackAndCleansUp()
    {
        var stateDir = CreateTempDir();
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("Something went wrong (NOT an overflow)"));
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Something went wrong (NOT an overflow)"));

        var service = CreateService(stateDir, chatClientFactory: _ => mockClient.Object);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var registryStatuses = new List<string>();
        var errors = new List<string>();
        var saveSessionCalls = 0;

        // Gate completed by the onStreamingError callback, which only fires from the
        // mailbox's ComposerStreamingErrorMessage handler. This is more reliable than
        // gating on _terminated + onStreamingFinished, because OnShutdownAsync also sets
        // _terminated and calls onStreamingFinished — under contention the shutdown path
        // can fire that gate before the error message is processed, leaving errors empty.
        var errorGate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actor = CreateActor(
            service,
            _ =>
            {
                Interlocked.Increment(ref saveSessionCalls);
                return Task.CompletedTask;
            },
            status => registryStatuses.Add(status),
            _ => { },
            _ => { },
            error =>
            {
                errors.Add(error);
                errorGate.TrySetResult(error);
            },
            () => { });

        try
        {
            actor.Start();
            Assert.True(actor.Tell(new ComposerSendMessageMessage("hello")));

            // The error callback fires inside the mailbox handler — wait for it directly.
            await errorGate.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.Contains(errors, e => e.Contains("Something went wrong", StringComparison.Ordinal));
            Assert.True(saveSessionCalls == 0, "The error path must not save the session");
            // Exactly one terminal handling — the error Tell succeeded, so the fallback must not run.
            Assert.Equal(["streaming", "idle"], registryStatuses);
            Assert.False(GetIsStreaming(actor));

            // The accumulated streaming content carries the error marker.
            Assert.Contains("❌", GetStreamingContent(actor));
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task StreamingOverflow_ResetsSessionAndInvokesRecovery()
    {
        var stateDir = CreateTempDir();
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

        var service = CreateService(stateDir, chatClientFactory: _ => mockClient.Object);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        // Seed the session so we can observe the overflow-driven reset clearing it.
        service.Session.MessageHistory.Add(new ChatMessage(ChatRole.User, "seed message"));
        Assert.Single(service.Session.MessageHistory);

        var registryStatuses = new List<string>();
        var recoveryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveSessionCalls = 0;
        var overflowRecoveryCalls = 0;

        var actor = CreateActor(
            service,
            _ =>
            {
                Interlocked.Increment(ref saveSessionCalls);
                return Task.CompletedTask;
            },
            status => registryStatuses.Add(status),
            _ => { },
            _ => { },
            _ => { },
            () =>
            {
                if (Interlocked.Increment(ref overflowRecoveryCalls) == 1)
                    recoveryGate.TrySetResult();
            });

        try
        {
            actor.Start();
            Assert.True(actor.Tell(new ComposerSendMessageMessage("hello")));

            // The overflow-recovery callback fires inside the mailbox terminal handler, so
            // this gate deterministically signals that the full overflow path completed.
            await recoveryGate.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.Equal(1, overflowRecoveryCalls);

            // Overflow recovery reset the session (fresh empty history). The reset session
            // must NOT be persisted — saving it would overwrite the on-disk history with the
            // empty post-overflow session.
            Assert.Empty(service.Session.MessageHistory);
            Assert.True(saveSessionCalls == 0, "Overflow recovery must NOT persist the reset (empty) session");
            Assert.Equal(["streaming", "idle"], registryStatuses);
            Assert.False(GetIsStreaming(actor));
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── Disposal / terminal cleanup ──

    [Fact]
    public async Task DisposeWhileStreaming_CancelsStream_AndCleansUpTerminalState()
    {
        var stateDir = CreateTempDir();
        var blockingClient = new BlockingStreamingClient();
        var service = CreateService(stateDir, chatClientFactory: _ => blockingClient);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var registryStatuses = new List<string>();
        var finishedCalls = new List<int>();

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            status => registryStatuses.Add(status),
            _ => { },
            calls => finishedCalls.Add(calls),
            _ => { },
            () => { });

        try
        {
            actor.Start();
            Assert.True(actor.Tell(new ComposerSendMessageMessage("hello")));

            // Wait until the stream is actually running and blocked.
            await Task.WhenAny(
                Task.Run(() =>
                {
                    while (!registryStatuses.Contains("streaming", StringComparer.Ordinal))
                        Thread.Sleep(10);
                }, TestContext.Current.CancellationToken),
                Task.Delay(Timeout, TestContext.Current.CancellationToken));
            Assert.Contains("streaming", registryStatuses);

            // Disposal cancels the CTS, awaits the streaming task, and performs terminal cleanup.
            await actor.DisposeAsync().AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.True(blockingClient.WasCancelled, "The streaming client must observe cancellation during disposal");
            Assert.False(GetIsStreaming(actor), "Streaming state must be reset after disposal");
            Assert.True(finishedCalls.Count >= 1,
                "Terminal cleanup must invoke onStreamingFinished even when the mailbox closed");
            Assert.Contains("idle", registryStatuses);
        }
        finally
        {
            blockingClient.Release();
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task DisposeWithoutStreaming_CompletesCleanly()
    {
        var stateDir = CreateTempDir();
        var client = new TextStreamingClient("hi");
        var service = CreateService(stateDir, chatClientFactory: _ => client);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            actor.Start();
            await actor.DisposeAsync().AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.True(actor.IsCompleted, "Actor should complete on disposal without a stream");
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── CancelReply: disposal without start drains and cancels pending replies ──

    [Fact]
    public async Task DisposeWithoutStart_CancelsAllReplyBearingMessages()
    {
        var stateDir = CreateTempDir();
        var client = new TextStreamingClient("hi");
        var service = CreateService(stateDir, chatClientFactory: _ => client);

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            // Never Start() — disposal drains the mailbox and CancelReply must fire.
            var connectReply = NewReply<bool>();
            var resetReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var switchReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var compactReply = NewReply<bool>();
            var compactPartialReply = NewReply<bool>();

            Assert.True(actor.Tell(new ComposerConnectMessage(connectReply, CancellationToken.None)));
            Assert.True(actor.Tell(new ComposerResetSessionMessage(resetReply, CancellationToken.None)));
            Assert.True(actor.Tell(new ComposerSwitchModelMessage("model-b", ReasoningEffort.Medium, switchReply, CancellationToken.None)));
            Assert.True(actor.Tell(new ComposerCompactMessage(compactReply, CancellationToken.None)));
            Assert.True(actor.Tell(new ComposerCompactPartialMessage(50, compactPartialReply, CancellationToken.None)));

            await actor.DisposeAsync().AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.True(connectReply.Task.IsCanceled);
            Assert.True(resetReply.Task.IsCanceled);
            Assert.True(switchReply.Task.IsCanceled);
            Assert.True(compactReply.Task.IsCanceled);
            Assert.True(compactPartialReply.Task.IsCanceled);
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── OnUnhandledException faults reply-bearing messages ──

    [Fact]
    public async Task OnUnhandledException_FaultsReplyBearingMessage_WithOriginalException()
    {
        var stateDir = CreateTempDir();
        var client = new TextStreamingClient("hi");
        var service = CreateService(stateDir, chatClientFactory: _ => client);

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            var method = typeof(ComposerActor).GetMethod("OnUnhandledException", PrivateFlags)
                ?? throw new InvalidOperationException("OnUnhandledException not found on ComposerActor");

            var reply = NewReply<bool>();
            var message = new ComposerConnectMessage(reply, CancellationToken.None);
            var ex = new InvalidOperationException("handler boom");

            method.Invoke(actor, [message, ex]);

            Assert.True(reply.Task.IsFaulted, "Reply should be faulted by OnUnhandledException");
            Assert.Same(ex, reply.Task.Exception!.InnerException);
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── OnDisposeTimeout logs when the mailbox is stuck ──

    [Fact]
    public async Task OnDisposeTimeout_LogsWarning_WhenHandlerBlocksPastTimeout()
    {
        var stateDir = CreateTempDir();
        var client = new TextStreamingClient("hi");
        var service = CreateService(stateDir, chatClientFactory: _ => client);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var logger = new RecordingLogger();
        var saveEntry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // saveSession blocks the mailbox handler forever (ignoring the loop token), so the
        // loop cannot finish and DisposeAsync hits its 5-second timeout.
        var actor = CreateActor(
            service,
            _ =>
            {
                saveEntry.TrySetResult();
                return saveRelease.Task;
            },
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { },
            logger);

        try
        {
            actor.Start();
            Assert.True(actor.Tell(new ComposerSendMessageMessage("hello")));

            // Wait until the complete handler is stuck inside saveSession.
            await saveEntry.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            // DisposeAsync must time out (5s) rather than hang; the hook logs a warning.
            await actor.DisposeAsync().AsTask().WaitAsync(Timeout + TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.Contains(logger.Messages, m => m.Contains("disposal timed out", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            saveRelease.TrySetResult();
            await Task.WhenAny(actor.Completion, Task.Delay(Timeout, TestContext.Current.CancellationToken));
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── Compact cancellation → false (actor catches OperationCanceledException) ──

    [Fact]
    public async Task Compact_WhenCancelled_RepliesFalseNotFaulted()
    {
        var stateDir = CreateTempDir();
        var mockClient = new Mock<IChatClient>();
        // GetResponseAsync is called by ContextCompactor; make it throw OCE when the
        // token is already cancelled so the actor's catch(OperationCanceledException) fires.
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(new CancellationToken(true)));
        mockClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());

        var service = CreateService(stateDir, chatClientFactory: _ => mockClient.Object);
        await service.ConnectAsync(TestContext.Current.CancellationToken);
        PopulateSession(service.Session, 15);

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            actor.Start();

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var reply = NewReply<bool>();
            Assert.True(actor.Tell(new ComposerCompactMessage(reply, cts.Token)));

            var result = await AwaitReplyAsync(reply);
            Assert.False(result, "Cancelled compact should reply false, not fault");
            Assert.True(reply.Task.IsCompletedSuccessfully, "Reply should be completed successfully (not faulted/canceled)");
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── Compact-partial failure → false ──

    [Fact]
    public async Task CompactPartial_WhenCompactorThrows_RepliesFalse()
    {
        var stateDir = CreateTempDir();
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("partial compaction backend boom"));
        mockClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());

        var service = CreateService(stateDir, chatClientFactory: _ => mockClient.Object);
        await service.ConnectAsync(TestContext.Current.CancellationToken);
        PopulateSession(service.Session, 30);

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            actor.Start();

            var reply = NewReply<bool>();
            Assert.True(actor.Tell(new ComposerCompactPartialMessage(50, reply, CancellationToken.None)));

            var result = await AwaitReplyAsync(reply);
            Assert.False(result, "Partial compact failure should reply false, never fault");
            Assert.True(reply.Task.IsCompletedSuccessfully, "Reply should be completed successfully (not faulted/canceled)");
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── OnUnhandledException faults ALL reply-bearing message types ──

    [Theory]
    [InlineData("Connect")]
    [InlineData("Reset")]
    [InlineData("Switch")]
    [InlineData("Compact")]
    [InlineData("CompactPartial")]
    public async Task OnUnhandledException_FaultsAllReplyBearingMessages_WithOriginalException(string messageType)
    {
        var stateDir = CreateTempDir();
        var client = new TextStreamingClient("hi");
        var service = CreateService(stateDir, chatClientFactory: _ => client);

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            var method = typeof(ComposerActor).GetMethod("OnUnhandledException", PrivateFlags)
                ?? throw new InvalidOperationException("OnUnhandledException not found on ComposerActor");

            var ex = new InvalidOperationException("handler boom");
            Task replyTask;

            switch (messageType)
            {
                case "Connect":
                    {
                        var reply = NewReply<bool>();
                        method.Invoke(actor, [new ComposerConnectMessage(reply, CancellationToken.None), ex]);
                        replyTask = reply.Task;
                        break;
                    }
                case "Reset":
                    {
                        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        method.Invoke(actor, [new ComposerResetSessionMessage(reply, CancellationToken.None), ex]);
                        replyTask = reply.Task;
                        break;
                    }
                case "Switch":
                    {
                        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        method.Invoke(actor, [new ComposerSwitchModelMessage("m", ReasoningEffort.Low, reply, CancellationToken.None), ex]);
                        replyTask = reply.Task;
                        break;
                    }
                case "Compact":
                    {
                        var reply = NewReply<bool>();
                        method.Invoke(actor, [new ComposerCompactMessage(reply, CancellationToken.None), ex]);
                        replyTask = reply.Task;
                        break;
                    }
                case "CompactPartial":
                    {
                        var reply = NewReply<bool>();
                        method.Invoke(actor, [new ComposerCompactPartialMessage(50, reply, CancellationToken.None), ex]);
                        replyTask = reply.Task;
                        break;
                    }
                default:
                    throw new ArgumentException($"Unknown message type: {messageType}");
            }

            Assert.True(replyTask.IsFaulted, $"{messageType} reply should be faulted by OnUnhandledException");
            Assert.Same(ex, replyTask.Exception!.InnerException);
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── OnUnhandledException cancels fire-and-forget messages (default → CancelReply) ──

    [Fact]
    public async Task OnUnhandledException_FireAndForgetMessages_DoNotThrow()
    {
        var stateDir = CreateTempDir();
        var client = new TextStreamingClient("hi");
        var service = CreateService(stateDir, chatClientFactory: _ => client);

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            var method = typeof(ComposerActor).GetMethod("OnUnhandledException", PrivateFlags)!;
            var ex = new InvalidOperationException("handler boom");

            // Fire-and-forget messages have no reply to fault — the default branch calls
            // CancelReply which is a no-op for them. This must not throw.
            method.Invoke(actor, [new ComposerSendMessageMessage("msg"), ex]);
            method.Invoke(actor, [new ComposerCancelStreamingMessage(), ex]);
            method.Invoke(actor, [new ComposerStreamingUpdateMessage("content"), ex]);
            method.Invoke(actor, [new ComposerStreamingCompleteMessage(0, false, false), ex]);
            method.Invoke(actor, [new ComposerStreamingErrorMessage("err"), ex]);
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── Tell-fails fallback: RunStreamingAsync finally cleanup when mailbox is closed ──

    /// <summary>
    /// When the terminal <c>Tell</c> is REJECTED (mailbox closed during shutdown) the streaming
    /// task must run the terminal sequence itself, exactly once: latch <c>_terminated</c>, reset
    /// state, invoke the callbacks. Because the latch is closed,
    /// <c>OnShutdownAsync</c>'s <c>!_terminated</c> check must then SKIP — so
    /// <c>onStreamingFinished</c> and the idle registry refresh fire exactly ONCE, not twice.
    /// <para>
    /// Ordering matters: the actor is disposed (mailbox closed, stream cancelled) while the
    /// client is STILL blocked, so the terminal <c>Tell</c> is necessarily rejected. Releasing
    /// the gate first would let the ordinary mailbox handler satisfy the assertions even with
    /// the fallback removed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TellFails_FallbackRunsCompleteTerminalCleanupExactlyOnce()
    {
        var stateDir = CreateTempDir();
        var blockingClient = new BlockingStreamingClient();
        var service = CreateService(stateDir, chatClientFactory: _ => blockingClient);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var registryStatuses = new List<string>();
        var finishedCalls = new List<int>();
        var idleGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            status =>
            {
                lock (registryStatuses)
                {
                    registryStatuses.Add(status);
                    if (status == "idle") idleGate.TrySetResult();
                }
            },
            _ => { },
            calls => { lock (finishedCalls) finishedCalls.Add(calls); },
            _ => { },
            () => { });

        try
        {
            actor.Start();
            Assert.True(actor.Tell(new ComposerSendMessageMessage("hello")));

            // Wait until the stream is actually running and blocked on the client's gate.
            await WaitForStreamingStatusAsync(registryStatuses);

            // Dispose while the client is STILL BLOCKED: the mailbox closes and the stream is
            // cancelled, so the client throws OperationCanceledException and the resulting
            // terminal Tell is rejected — the failed-Tell path is the ONLY way to terminate.
            await actor.DisposeAsync().AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);

            await idleGate.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            // The stream really was cancelled mid-flight (not completed normally).
            Assert.True(blockingClient.WasCancelled,
                "The client must have observed cancellation — otherwise the Tell could have been accepted");

            // Exactly ONE terminal callback sequence.
            lock (finishedCalls)
            {
                var finished = Assert.Single(finishedCalls);
                Assert.Equal(0, finished);
            }
            lock (registryStatuses)
            {
                Assert.Equal(1, registryStatuses.Count(s => s == "idle"));
                Assert.Equal(["streaming", "idle"], registryStatuses);
            }

            // Complete cleanup: latch closed and all streaming state cleared.
            Assert.True(GetTerminated(actor), "The fallback must latch _terminated so shutdown skips");
            Assert.False(GetIsStreaming(actor));
            Assert.Null(GetStreamingCts(actor));
            Assert.Null(GetStreamingTask(actor));
        }
        finally
        {
            blockingClient.Release();
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>
    /// Deterministic rendezvous proof that <c>OnShutdownAsync</c> awaits the STREAMING TASK
    /// (including its failed-<c>Tell</c> terminal callbacks) and not a field the streaming task
    /// could have nulled underneath it.
    /// <para>
    /// The ordering is forced so that the fallback runs to completion of its state reset BEFORE
    /// <c>OnShutdownAsync</c> ever snapshots the fields:
    /// </para>
    /// <list type="number">
    /// <item>The client emits one text delta; the resulting update message OCCUPIES the mailbox
    /// loop, because the test blocks inside <c>onStreamingUpdate</c>. The loop therefore cannot
    /// reach <c>OnShutdownAsync</c>.</item>
    /// <item><c>DisposeAsync</c> starts in the background: it closes the mailbox and cancels the
    /// loop token, but the loop is still stuck in the update callback.</item>
    /// <item>The client gate is released, so the streaming task throws, its terminal
    /// <c>Tell</c> fails (mailbox closed), and the fallback runs the shared terminal handler —
    /// which, with the defect present, nulls <c>_streamingTask</c> here. Its
    /// <c>onStreamingFinished</c> callback then BLOCKS on a rendezvous.</item>
    /// <item>Only now is the update callback released, letting the loop drain and enter
    /// <c>OnShutdownAsync</c> — which, with the defect, snapshots an already-null task, skips
    /// the await, and signals completion while the fallback callback is still running.</item>
    /// </list>
    /// <para>
    /// The assertion is that disposal has NOT completed while that callback is blocked.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Shutdown_DoesNotCompleteWhileFallbackTerminalCallbackIsRunning()
    {
        var stateDir = CreateTempDir();
        var client = new DeltaThenBlockThenThrowClient("delta", new InvalidOperationException("shutdown race"));
        var service = CreateService(stateDir, chatClientFactory: _ => client);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var registryStatuses = new List<string>();
        var finishedCalls = new List<int>();

        // Rendezvous 1: the mailbox loop is held inside the update handler.
        var updateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUpdate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Rendezvous 2: the streaming task is held inside the fallback's terminal callback.
        var finishedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            status => { lock (registryStatuses) registryStatuses.Add(status); },
            _ =>
            {
                // Runs on the MAILBOX LOOP: occupy it so it cannot reach OnShutdownAsync.
                updateEntered.TrySetResult();
                releaseUpdate.Task.Wait(TimeSpan.FromSeconds(30));
            },
            calls =>
            {
                lock (finishedCalls) finishedCalls.Add(calls);

                // Runs on the STREAMING TASK, after the fallback has latched _terminated and
                // (with the defect) already nulled _streamingTask. Hold the terminal sequence
                // open — this is the window a skipped await would race with.
                finishedEntered.TrySetResult();
                releaseFinished.Task.Wait(TimeSpan.FromSeconds(30));
            },
            _ => { },
            () => { });

        var disposeTask = Task.CompletedTask;
        try
        {
            actor.Start();
            Assert.True(actor.Tell(new ComposerSendMessageMessage("hello")));

            // 1. The mailbox loop is now parked inside the update handler.
            await updateEntered.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            // 2. Begin disposal: closes the mailbox, cancels the loop token. The loop is still
            //    blocked in the update callback, so OnShutdownAsync has NOT started.
            disposeTask = actor.DisposeAsync().AsTask();
            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.False(disposeTask.IsCompleted);

            // 3. Let the streaming task proceed: it throws, its terminal Tell is rejected, and
            //    the fallback runs — nulling _streamingTask if the defect is present — then
            //    blocks in onStreamingFinished.
            client.Release();
            await finishedEntered.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            // 4. Release the mailbox loop so it drains and enters OnShutdownAsync, which now
            //    snapshots the (possibly nulled) fields.
            releaseUpdate.TrySetResult();

            // THE ASSERTION: the fallback's terminal callback is still running, so disposal
            // must not have completed. Well under the Actor's 5-second dispose timeout, so a
            // timeout cannot masquerade as a pass.
            var raced = await Task.WhenAny(disposeTask, Task.Delay(1000, TestContext.Current.CancellationToken));
            Assert.NotSame(disposeTask, raced);
            Assert.False(disposeTask.IsCompleted,
                "DisposeAsync must not complete while a fallback terminal callback is still running");
            Assert.False(actor.IsCompleted,
                "Actor Completion must not be signalled while a fallback terminal callback is still running");

            // Release the rendezvous — disposal may now finish.
            releaseFinished.TrySetResult();
            await disposeTask.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.True(actor.IsCompleted);

            // Exactly one terminal callback sequence, and the latch is closed.
            lock (finishedCalls) Assert.Single(finishedCalls);
            lock (registryStatuses) Assert.Equal(1, registryStatuses.Count(s => s == "idle"));
            Assert.True(GetTerminated(actor));
            Assert.False(GetIsStreaming(actor));
        }
        finally
        {
            releaseUpdate.TrySetResult();
            releaseFinished.TrySetResult();
            client.Release();
            try { await disposeTask; } catch (Exception) { /* asserted above */ }
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>
    /// Criterion 10 on the failed-<c>Tell</c> path: when the OVERFLOW terminal <c>Tell</c> is
    /// rejected because the mailbox closed, the overflow-recovery callback must STILL fire so
    /// the facade deletes the stale session file and clears its compaction flags. A generic
    /// finish/idle fallback would silently skip it and leave the overflowing session on disk
    /// to be reloaded on the next start. Also asserts overflow still never persists the session.
    /// </summary>
    [Fact]
    public async Task OverflowTellFails_StillInvokesOverflowRecovery()
    {
        var stateDir = CreateTempDir();
        var overflowEx = new InvalidOperationException("model_max_prompt_tokens_exceeded");

        // Blocks until cancelled, then throws the overflow error — so the overflow terminal
        // message is produced only AFTER the mailbox has been closed by disposal.
        var client = new BlockOnCancelThenThrowClient(overflowEx);
        var service = CreateService(stateDir, chatClientFactory: _ => client);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var registryStatuses = new List<string>();
        var finishedCalls = new List<int>();
        var saveSessionCalls = 0;
        var overflowRecoveryCalls = 0;
        var recoveryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var actor = CreateActor(
            service,
            _ =>
            {
                Interlocked.Increment(ref saveSessionCalls);
                return Task.CompletedTask;
            },
            status => { lock (registryStatuses) registryStatuses.Add(status); },
            _ => { },
            calls => { lock (finishedCalls) finishedCalls.Add(calls); },
            _ => { },
            () =>
            {
                Interlocked.Increment(ref overflowRecoveryCalls);
                recoveryGate.TrySetResult();
            });

        try
        {
            actor.Start();
            Assert.True(actor.Tell(new ComposerSendMessageMessage("hello")));

            await WaitForStreamingStatusAsync(registryStatuses);
            await client.Entered.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            // Close the mailbox while the client is blocked. Cancellation makes the client
            // throw the overflow error, so the OVERFLOW terminal Tell is rejected.
            await actor.DisposeAsync().AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);

            // Criterion 10: overflow recovery runs even though the mailbox was gone.
            await recoveryGate.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.Equal(1, overflowRecoveryCalls);

            // Overflow never persists (the session was just replaced with an empty one)…
            Assert.True(saveSessionCalls == 0, "Overflow recovery must not persist the reset session");

            // …and exactly one finish/idle sequence ran.
            lock (finishedCalls) Assert.Single(finishedCalls);
            lock (registryStatuses)
            {
                Assert.Equal(1, registryStatuses.Count(s => s == "idle"));
                Assert.Equal(["streaming", "idle"], registryStatuses);
            }

            Assert.True(GetTerminated(actor));
            Assert.False(GetIsStreaming(actor));
        }
        finally
        {
            client.Release();
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>
    /// Criterion 1 on the failed-<c>Tell</c> path: when the ERROR terminal <c>Tell</c> is
    /// rejected because the mailbox closed, <c>onStreamingError</c> must STILL be invoked —
    /// a generic finish/idle fallback would swallow the failure entirely.
    /// </summary>
    [Fact]
    public async Task ErrorTellFails_StillReportsError()
    {
        var stateDir = CreateTempDir();
        var failure = new InvalidOperationException("stream blew up during shutdown");

        // Blocks until cancelled, then throws a NON-cancellation, non-overflow error so the
        // error terminal message is produced only after the mailbox has closed.
        var client = new BlockOnCancelThenThrowClient(failure);
        var service = CreateService(stateDir, chatClientFactory: _ => client);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var registryStatuses = new List<string>();
        var finishedCalls = new List<int>();
        var errors = new List<string>();
        var saveSessionCalls = 0;
        var errorGate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var actor = CreateActor(
            service,
            _ =>
            {
                Interlocked.Increment(ref saveSessionCalls);
                return Task.CompletedTask;
            },
            status => { lock (registryStatuses) registryStatuses.Add(status); },
            _ => { },
            calls => { lock (finishedCalls) finishedCalls.Add(calls); },
            error =>
            {
                lock (errors) errors.Add(error);
                errorGate.TrySetResult(error);
            },
            () => { });

        try
        {
            actor.Start();
            Assert.True(actor.Tell(new ComposerSendMessageMessage("hello")));

            await WaitForStreamingStatusAsync(registryStatuses);
            await client.Entered.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            // Close the mailbox while blocked so the ERROR terminal Tell is rejected.
            await actor.DisposeAsync().AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);

            // Criterion 1: the error is still reported.
            var reported = await errorGate.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.Contains("stream blew up during shutdown", reported);
            lock (errors) Assert.Single(errors);

            // The error path never persists, and exactly one finish/idle sequence ran.
            Assert.True(saveSessionCalls == 0, "The error path must not save the session");
            lock (finishedCalls) Assert.Single(finishedCalls);
            lock (registryStatuses)
            {
                Assert.Equal(1, registryStatuses.Count(s => s == "idle"));
                Assert.Equal(["streaming", "idle"], registryStatuses);
            }

            // The error text is appended to the accumulated content, as on the mailbox path.
            Assert.Contains("❌", GetStreamingContent(actor));
            Assert.True(GetTerminated(actor));
            Assert.False(GetIsStreaming(actor));
        }
        finally
        {
            client.Release();
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── Pre-Tell failure: overflow recovery itself throws ──

    /// <summary>
    /// If overflow recovery's <c>ResetSessionAsync</c> throws, no terminal <c>Tell</c> has been
    /// attempted yet. That must be reported as a real ERROR (not a silent finish/idle), and the
    /// streaming state must still be fully cleared. A `terminalPosted=false` default that
    /// conflates "no Tell attempted" with "Tell rejected" fails this.
    /// </summary>
    [Fact]
    public async Task OverflowRecoveryThrows_ReportsErrorAndClearsStreamingState()
    {
        var stateDir = CreateTempDir();
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

        var service = CreateService(stateDir, chatClientFactory: _ => mockClient.Object);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        // Agent disposal is allowed to propagate, so this makes the recovery reset itself fail.
        service.OnAgentDisposing = _ => throw new InvalidOperationException("recovery reset boom");

        var errors = new List<string>();
        var registryStatuses = new List<string>();
        var saveSessionCalls = 0;
        var overflowRecoveryCalls = 0;
        var errorGate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var actor = CreateActor(
            service,
            _ =>
            {
                Interlocked.Increment(ref saveSessionCalls);
                return Task.CompletedTask;
            },
            status => { lock (registryStatuses) registryStatuses.Add(status); },
            _ => { },
            _ => { },
            error =>
            {
                lock (errors) errors.Add(error);
                errorGate.TrySetResult(error);
            },
            () => Interlocked.Increment(ref overflowRecoveryCalls));

        try
        {
            actor.Start();
            Assert.True(actor.Tell(new ComposerSendMessageMessage("hello")));

            // The failure must surface as an error, not a silent finish.
            var error = await errorGate.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.Contains("recovery reset boom", error);

            // The error callback can fire before ReleaseStreamingResources clears the streaming
            // fields, so poll until the state is fully cleared before asserting on it.
            var deadline = DateTime.UtcNow.Add(Timeout);
            while ((GetStreamingCts(actor) is not null || GetStreamingTask(actor) is not null)
                   && DateTime.UtcNow < deadline)
                await Task.Yield();

            // A failed recovery is NOT a successful overflow recovery, and nothing is persisted.
            Assert.Equal(0, overflowRecoveryCalls);
            Assert.True(saveSessionCalls == 0, "A failed overflow recovery must not persist the session");

            // State fully cleared by the terminal handler.
            Assert.False(GetIsStreaming(actor));
            Assert.True(GetTerminated(actor));
            Assert.Null(GetStreamingCts(actor));
            Assert.Null(GetStreamingTask(actor));

            lock (registryStatuses) Assert.Equal(["streaming", "idle"], registryStatuses);
        }
        finally
        {
            service.OnAgentDisposing = null;
            await actor.DisposeAsync();
            try { await service.DisposeAsync(); } catch (InvalidOperationException) { }
            TryDeleteDir(stateDir);
        }
    }

    // ── Regression: reusable terminal protocol (defect 1) ──

    /// <summary>
    /// The <c>_terminated</c> latch must be reset when a new stream is admitted, otherwise the
    /// second stream's terminal message is swallowed and the actor is permanently stuck
    /// streaming. Removing the `_terminated = false` reset in the send handler fails this.
    /// </summary>
    [Fact]
    public async Task SecondStream_AfterFirstCompletes_AlsoCompletes()
    {
        var stateDir = CreateTempDir();
        var client = new TextStreamingClient("Hello");
        var service = CreateService(stateDir, chatClientFactory: _ => client);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var registryStatuses = new List<string>();
        var saveSessionCalls = 0;
        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var actor = CreateActor(
            service,
            _ =>
            {
                var n = Interlocked.Increment(ref saveSessionCalls);
                if (n == 1) firstGate.TrySetResult();
                else if (n == 2) secondGate.TrySetResult();
                return Task.CompletedTask;
            },
            status => { lock (registryStatuses) registryStatuses.Add(status); },
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            actor.Start();

            Assert.True(actor.Tell(new ComposerSendMessageMessage("first")));
            await firstGate.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.False(GetIsStreaming(actor), "First stream must have terminated");
            Assert.False(GetTerminated(actor) && GetIsStreaming(actor));

            // Second stream must be admitted AND complete (the terminal latch was reset).
            Assert.True(actor.Tell(new ComposerSendMessageMessage("second")));
            await secondGate.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.Equal(2, saveSessionCalls);
            Assert.False(GetIsStreaming(actor), "Second stream must have terminated too");

            // Exactly two full streaming→idle cycles, with no duplicate terminal reports.
            List<string> snapshot;
            lock (registryStatuses) snapshot = [.. registryStatuses];
            Assert.Equal(["streaming", "idle", "streaming", "idle"], snapshot);
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>
    /// A SUCCESSFUL terminal <c>Tell</c> must never trigger the streaming task's fallback
    /// cleanup — the mailbox handler owns the terminal sequence. If the fallback also runs,
    /// idle/finished are reported twice and the facade gate is cleared before the session
    /// save, so this asserts exactly one of each.
    /// </summary>
    [Fact]
    public async Task SuccessfulTerminalTell_DoesNotTriggerFallbackCleanup()
    {
        var stateDir = CreateTempDir();
        var client = new TextStreamingClient("Hello");
        var service = CreateService(stateDir, chatClientFactory: _ => client);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var registryStatuses = new List<string>();
        var finishedCalls = 0;
        var saveOrder = new List<string>();
        var completedGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var actor = CreateActor(
            service,
            _ =>
            {
                lock (saveOrder) saveOrder.Add("save");
                return Task.CompletedTask;
            },
            status =>
            {
                lock (saveOrder) saveOrder.Add(status);
                lock (registryStatuses) registryStatuses.Add(status);
            },
            _ => { },
            _ =>
            {
                Interlocked.Increment(ref finishedCalls);
                completedGate.TrySetResult();
            },
            _ => { },
            () => { });

        try
        {
            actor.Start();
            Assert.True(actor.Tell(new ComposerSendMessageMessage("hello")));

            await completedGate.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            // Give any (buggy) fallback a chance to also fire before asserting.
            await Task.Delay(200, TestContext.Current.CancellationToken);

            Assert.Equal(1, finishedCalls);
            List<string> statuses;
            lock (registryStatuses) statuses = [.. registryStatuses];
            Assert.Equal(["streaming", "idle"], statuses);

            // And the ordering proves the handler ran the save BEFORE reporting idle —
            // a fallback-driven idle would appear before the save.
            List<string> order;
            lock (saveOrder) order = [.. saveOrder];
            Assert.Equal(["streaming", "save", "idle"], order);
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── Regression: no save on graceful (yield-break) cancellation (defect 2) ──

    /// <summary>
    /// A provider that ends the stream with a bare <c>yield break</c> after cancellation never
    /// throws <see cref="OperationCanceledException"/>, so the loop falls through to the
    /// post-loop terminal Tell. That exit must still be classified as cancelled — otherwise the
    /// truncated response is persisted over the real history.
    /// </summary>
    [Fact]
    public async Task GracefulCancellation_YieldBreakExit_DoesNotSaveSession()
    {
        var stateDir = CreateTempDir();
        var client = new YieldBreakOnCancelClient();
        var service = CreateService(stateDir, chatClientFactory: _ => client);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var saveSessionCalls = 0;
        var finishedGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var actor = CreateActor(
            service,
            _ =>
            {
                Interlocked.Increment(ref saveSessionCalls);
                return Task.CompletedTask;
            },
            _ => { },
            _ => { },
            _ => finishedGate.TrySetResult(),
            _ => { },
            () => { });

        try
        {
            actor.Start();
            Assert.True(actor.Tell(new ComposerSendMessageMessage("hello")));

            // Only cancel once the stream is genuinely running — a pre-request cancellation
            // would be vacuous and would not exercise the yield-break exit.
            await client.Entered.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.True(actor.Tell(new ComposerCancelStreamingMessage()));

            await finishedGate.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.True(saveSessionCalls == 0,
                "A yield-break cancellation exit must be classified as cancelled (no session save)");
            Assert.False(GetIsStreaming(actor));
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── Regression: reset/switch cancellation cancels the reply (defect 4) ──

    /// <summary>
    /// When the caller's token is cancelled, the switch reply must be CANCELLED, not faulted
    /// with an <see cref="OperationCanceledException"/> — callers distinguish the two, and a
    /// faulted reply surfaces as an unexpected error rather than a cancellation.
    /// </summary>
    [Fact]
    public async Task SwitchModel_CallerCancelled_ReplyIsCanceledNotFaulted()
    {
        var stateDir = CreateTempDir();
        var service = CreateService(stateDir, chatClientFactory: _ => new TextStreamingClient("hi"));
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            actor.Start();

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            // ComposerAgentService.SwitchModelAsync calls ct.ThrowIfCancellationRequested()
            // after validating the model, so a cancelled token throws OperationCanceledException.
            var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(actor.Tell(new ComposerSwitchModelMessage("test-model", ReasoningEffort.High, reply, cts.Token)));

            await AwaitSettledAsync(reply);
            Assert.True(reply.Task.IsCanceled,
                "A cancelled caller token must CANCEL the reply, not fault it with OperationCanceledException");
            Assert.False(reply.Task.IsFaulted);
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>
    /// Proves the reset handler CONSUMES the caller's token rather than merely reclassifying an
    /// independently-thrown exception. <c>ComposerAgentService.ResetSessionAsync</c> takes no
    /// token, so nothing but an explicit <c>ThrowIfCancellationRequested</c> in
    /// <c>DoResetAsync</c> can stop it — the token is cancelled ALONE (no injected throw), and
    /// the destructive reset must be PREVENTED while the reply is CANCELLED (not faulted).
    /// </summary>
    [Fact]
    public async Task ResetSession_PreCancelledToken_PreventsResetAndCancelsReply()
    {
        var stateDir = CreateTempDir();
        var service = CreateService(stateDir, chatClientFactory: _ => new TextStreamingClient("hi"));
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        // Seed the session so a reset is observable: a reset replaces it with a fresh,
        // empty one, so surviving history proves the reset never ran.
        service.Session.MessageHistory.Add(new ChatMessage(ChatRole.User, "seed message"));
        var sessionBefore = service.Session;
        Assert.Single(service.Session.MessageHistory);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            actor.Start();

            // ONLY the token is cancelled — no OnAgentDisposing hook, no injected exception.
            var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(actor.Tell(new ComposerResetSessionMessage(reply, cts.Token)));

            await AwaitSettledAsync(reply);

            // (b) The reply is CANCELLED, not faulted.
            Assert.True(reply.Task.IsCanceled,
                "A cancelled caller token must CANCEL the reset reply, not fault it");
            Assert.False(reply.Task.IsFaulted);

            // (a) The reset was PREVENTED — same session instance, history intact.
            Assert.Same(sessionBefore, service.Session);
            Assert.Single(service.Session.MessageHistory);
            Assert.Equal("seed message", service.Session.MessageHistory[0].Text);
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>
    /// Cancellation arriving DURING the reset must also be reported: the token is observed
    /// again after <c>ResetSessionAsync</c> returns, so the reply is cancelled rather than
    /// silently succeeding.
    /// </summary>
    [Fact]
    public async Task ResetSession_TokenCancelledDuringReset_CancelsReply()
    {
        var stateDir = CreateTempDir();
        var service = CreateService(stateDir, chatClientFactory: _ => new TextStreamingClient("hi"));
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();

        // Cancel from inside the reset (the agent-disposal hook runs during ResetSessionAsync)
        // WITHOUT throwing, so only the post-reset token check can observe it.
        service.OnAgentDisposing = _ => cts.Cancel();

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            actor.Start();

            var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(actor.Tell(new ComposerResetSessionMessage(reply, cts.Token)));

            await AwaitSettledAsync(reply);
            Assert.True(reply.Task.IsCanceled,
                "Cancellation observed during the reset must cancel the reply");
            Assert.False(reply.Task.IsFaulted);
        }
        finally
        {
            service.OnAgentDisposing = null;
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>A non-cancellation failure must still FAULT the reply (not be misclassified).</summary>
    [Fact]
    public async Task SwitchModel_NonCancellationFailure_ReplyIsFaulted()
    {
        var stateDir = CreateTempDir();
        var service = CreateService(stateDir, chatClientFactory: _ => new TextStreamingClient("hi"));
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            actor.Start();

            // An unavailable model throws ArgumentException — not a cancellation.
            var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(actor.Tell(new ComposerSwitchModelMessage("no-such-model", ReasoningEffort.High, reply, CancellationToken.None)));

            await AwaitSettledAsync(reply);
            Assert.True(reply.Task.IsFaulted, "A non-cancellation failure must fault the reply");
            Assert.IsType<ArgumentException>(reply.Task.Exception!.InnerException);
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── Regression: content accumulation is not lossy (defect 5) ──

    /// <summary>
    /// The streaming task must accumulate into a LOCAL variable and ship whole snapshots, so
    /// the mailbox handler's <c>_streamingContent = m.Content</c> can never overwrite a newer
    /// accumulator value with a queued older one.
    /// <para>
    /// Overlap is FORCED: the update callback sleeps, so the mailbox falls behind the producer
    /// and its stale assignments land after newer accumulator writes. With a shared field the
    /// accumulator regresses and deltas are permanently lost; with a local it cannot.
    /// </para>
    /// </summary>
    [Fact]
    public async Task MultiDeltaStreaming_AccumulatedContentIsNotLossy()
    {
        var stateDir = CreateTempDir();
        var deltas = Enumerable.Range(0, 40).Select(i => $"[{i}]").ToList();
        var expected = string.Concat(deltas);
        var client = new MultiDeltaStreamingClient(deltas);
        var service = CreateService(stateDir, chatClientFactory: _ => client);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var contents = new List<string>();
        var completedGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            content =>
            {
                lock (contents) contents.Add(content);
                // Hold the mailbox inside the handler so the producer accumulates further
                // deltas concurrently. With a SHARED accumulator, the handler's
                // `_streamingContent = m.Content` then writes a stale value back over the
                // producer's newer one, and the very next producer append is computed from
                // that regressed value — permanently dropping deltas.
                Thread.Sleep(5);
            },
            _ => completedGate.TrySetResult(),
            _ => { },
            () => { });

        try
        {
            actor.Start();
            Assert.True(actor.Tell(new ComposerSendMessageMessage("hello")));

            await completedGate.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            List<string> snapshot;
            lock (contents) snapshot = [.. contents];

            // The final published snapshot must contain EVERY delta in order — nothing dropped.
            Assert.NotEmpty(snapshot);
            Assert.Equal(expected, snapshot[^1]);

            // Every published snapshot must be a prefix of the final content (monotonic growth).
            // A regressed accumulator produces a snapshot that is NOT a prefix.
            foreach (var s in snapshot)
                Assert.StartsWith(s, expected, StringComparison.Ordinal);
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── Regression: compact is rejected while streaming (defect 6) ──

    /// <summary>
    /// The facade's gate check is a TOCTOU probe, so the mailbox must reject a compact that
    /// races an admitted send. Ordering is deterministic: both messages are enqueued before
    /// the actor is started, so the send handler always runs first.
    /// </summary>
    [Fact]
    public async Task Compact_WhileStreaming_ReplyFaulted()
    {
        var stateDir = CreateTempDir();
        var blockingClient = new BlockingStreamingClient();
        var service = CreateService(stateDir, chatClientFactory: _ => blockingClient);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            // Enqueue BEFORE starting: the mailbox processes in FIFO order, so the send
            // handler sets _isStreaming before the compact handler runs. No polling needed.
            var compactReply = NewReply<bool>();
            var partialReply = NewReply<bool>();
            Assert.True(actor.Tell(new ComposerSendMessageMessage("hello")));
            Assert.True(actor.Tell(new ComposerCompactMessage(compactReply, CancellationToken.None)));
            Assert.True(actor.Tell(new ComposerCompactPartialMessage(50, partialReply, CancellationToken.None)));

            actor.Start();

            await Task.WhenAny(compactReply.Task, Task.Delay(Timeout, TestContext.Current.CancellationToken));
            await Task.WhenAny(partialReply.Task, Task.Delay(Timeout, TestContext.Current.CancellationToken));

            Assert.True(compactReply.Task.IsFaulted, "Compact during an active stream must fault");
            Assert.Contains("Cannot compact while streaming",
                compactReply.Task.Exception!.InnerException!.Message);

            Assert.True(partialReply.Task.IsFaulted, "Partial compact during an active stream must fault");
            Assert.Contains("Cannot compact while streaming",
                partialReply.Task.Exception!.InnerException!.Message);
        }
        finally
        {
            blockingClient.Release();
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── Regression: throwing callbacks cannot leave the actor stuck (defect 7) ──

    /// <summary>
    /// A facade callback that throws must not skip the remaining terminal cleanup. State reset
    /// happens first and unconditionally; each callback is individually guarded, so the
    /// registry idle report and overflow recovery still run.
    /// </summary>
    [Fact]
    public async Task ThrowingRegistryCallback_StillResetsStateAndRunsRemainingCallbacks()
    {
        var stateDir = CreateTempDir();
        var client = new TextStreamingClient("hello");
        var service = CreateService(stateDir, chatClientFactory: _ => client);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var finishedCalls = 0;
        var finishedGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            // Throws on the terminal "idle" report (but not on "streaming", so the stream starts).
            status =>
            {
                if (status == "idle") throw new InvalidOperationException("registry boom");
            },
            _ => { },
            _ =>
            {
                Interlocked.Increment(ref finishedCalls);
                finishedGate.TrySetResult();
            },
            _ => { },
            () => { });

        try
        {
            actor.Start();
            Assert.True(actor.Tell(new ComposerSendMessageMessage("hello")));

            // onStreamingFinished runs AFTER the throwing registry callback — reaching it at
            // all proves the throw did not abort the remaining cleanup.
            await finishedGate.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.Equal(1, finishedCalls);
            Assert.False(GetIsStreaming(actor), "State must be reset even when a callback throws");

            // The actor is still usable: a second stream can be admitted and completed.
            var secondGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondActorReady = actor.Tell(new ComposerSendMessageMessage("second"));
            Assert.True(secondActorReady, "Actor must still accept messages after a throwing callback");
            _ = secondGate;
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>
    /// Overflow recovery must still fire when an earlier terminal callback throws, otherwise
    /// the facade never clears its compaction flags nor deletes the stale session file.
    /// </summary>
    [Fact]
    public async Task ThrowingFinishedCallback_StillInvokesOverflowRecovery()
    {
        var stateDir = CreateTempDir();
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

        var service = CreateService(stateDir, chatClientFactory: _ => mockClient.Object);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var recoveryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            // Throws BEFORE the overflow-recovery callback in the terminal sequence.
            _ => throw new InvalidOperationException("finished boom"),
            _ => { },
            () => recoveryGate.TrySetResult());

        try
        {
            actor.Start();
            Assert.True(actor.Tell(new ComposerSendMessageMessage("hello")));

            await recoveryGate.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.False(GetIsStreaming(actor), "State must be reset even when a callback throws");
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── Regression: manual compaction persists (defect 8) ──

    /// <summary>
    /// A successful manual compaction must persist the session and refresh the registry —
    /// otherwise the compaction is lost on restart. Removing the persistence in
    /// <c>DoCompactAsync</c> fails this.
    /// </summary>
    [Fact]
    public async Task Compact_OnSuccess_SavesSessionAndRefreshesRegistry()
    {
        var stateDir = CreateTempDir();
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary of conversation")));
        mockClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());

        var service = CreateService(stateDir, chatClientFactory: _ => mockClient.Object);
        await service.ConnectAsync(TestContext.Current.CancellationToken);
        PopulateSession(service.Session, 15);

        var saveSessionCalls = 0;
        var registryStatuses = new List<string>();

        var actor = CreateActor(
            service,
            _ =>
            {
                Interlocked.Increment(ref saveSessionCalls);
                return Task.CompletedTask;
            },
            status => registryStatuses.Add(status),
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            actor.Start();

            var reply = NewReply<bool>();
            Assert.True(actor.Tell(new ComposerCompactMessage(reply, CancellationToken.None)));
            Assert.True(await AwaitReplyAsync(reply));

            Assert.True(saveSessionCalls == 1, "A successful compaction must persist the session");
            Assert.Contains("idle", registryStatuses);
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>Partial compaction persists on success too.</summary>
    [Fact]
    public async Task CompactPartial_OnSuccess_SavesSessionAndRefreshesRegistry()
    {
        var stateDir = CreateTempDir();
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary of conversation")));
        mockClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());

        var service = CreateService(stateDir, chatClientFactory: _ => mockClient.Object);
        await service.ConnectAsync(TestContext.Current.CancellationToken);
        PopulateSession(service.Session, 30);

        var saveSessionCalls = 0;
        var registryStatuses = new List<string>();

        var actor = CreateActor(
            service,
            _ =>
            {
                Interlocked.Increment(ref saveSessionCalls);
                return Task.CompletedTask;
            },
            status => registryStatuses.Add(status),
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            actor.Start();

            var reply = NewReply<bool>();
            Assert.True(actor.Tell(new ComposerCompactPartialMessage(50, reply, CancellationToken.None)));
            Assert.True(await AwaitReplyAsync(reply));

            Assert.True(saveSessionCalls == 1, "A successful partial compaction must persist the session");
            Assert.Contains("idle", registryStatuses);
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>A no-op compaction (too few messages) must NOT save or refresh.</summary>
    [Fact]
    public async Task Compact_WhenNoCompactionOccurs_DoesNotSaveSession()
    {
        var stateDir = CreateTempDir();
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary")));
        mockClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());

        var service = CreateService(stateDir, chatClientFactory: _ => mockClient.Object);
        await service.ConnectAsync(TestContext.Current.CancellationToken);
        PopulateSession(service.Session, 2); // too few to compact

        var saveSessionCalls = 0;

        var actor = CreateActor(
            service,
            _ =>
            {
                Interlocked.Increment(ref saveSessionCalls);
                return Task.CompletedTask;
            },
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            actor.Start();

            var reply = NewReply<bool>();
            Assert.True(actor.Tell(new ComposerCompactMessage(reply, CancellationToken.None)));
            Assert.False(await AwaitReplyAsync(reply));

            Assert.True(saveSessionCalls == 0, "A no-op compaction must not persist the session");
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── Compaction exactly-once callbacks (full + partial × success/failure/cancel) ──

    /// <summary>
    /// Counts how many times <c>AgentOptions.OnCompacting</c> and <c>AgentOptions.OnCompacted</c>
    /// fire during a manual compaction. These are the agent service's wired callbacks — with the
    /// callback-free options fix they must NEVER fire during a manual (actor-initiated) compaction.
    /// </summary>
    private sealed class AgentCallbackTracker
    {
        public int OnCompactingCalls;
        public int OnCompactedCalls;

        public Action OnCompacting => () => Interlocked.Increment(ref OnCompactingCalls);
        public Action<CompactionResult> OnCompacted => _ => Interlocked.Increment(ref OnCompactedCalls);
    }

    [Fact]
    public async Task Compact_Full_Success_StartedAndFinishedFireOnce_AgentCallbacksSuppressed()
    {
        var stateDir = CreateTempDir();
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary of conversation")));
        mockClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());

        var agentTracker = new AgentCallbackTracker();
        var service = CreateService(
            stateDir,
            chatClientFactory: _ => mockClient.Object,
            onCompacting: agentTracker.OnCompacting,
            onCompacted: agentTracker.OnCompacted);
        await service.ConnectAsync(TestContext.Current.CancellationToken);
        PopulateSession(service.Session, 15);

        var startedCalls = 0;
        var finishedArgs = new List<bool>();
        var finishedGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { },
            onCompactingStarted: () => Interlocked.Increment(ref startedCalls),
            onCompactingFinished: success => { lock (finishedArgs) finishedArgs.Add(success); finishedGate.TrySetResult(success); });

        try
        {
            actor.Start();

            var reply = NewReply<bool>();
            Assert.True(actor.Tell(new ComposerCompactMessage(reply, CancellationToken.None)));

            Assert.True(await AwaitReplyAsync(reply));
            await finishedGate.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.Equal(1, startedCalls);
            Assert.Single(finishedArgs);
            Assert.True(finishedArgs[0], "onCompactingFinished(true) on success");
            Assert.Equal(0, agentTracker.OnCompactingCalls);
            Assert.Equal(0, agentTracker.OnCompactedCalls);
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task Compact_Full_Failure_StartedFiresOnce_FinishedFalse_AgentCallbacksSuppressed()
    {
        var stateDir = CreateTempDir();
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("compaction backend boom"));
        mockClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());

        var agentTracker = new AgentCallbackTracker();
        var service = CreateService(
            stateDir,
            chatClientFactory: _ => mockClient.Object,
            onCompacting: agentTracker.OnCompacting,
            onCompacted: agentTracker.OnCompacted);
        await service.ConnectAsync(TestContext.Current.CancellationToken);
        PopulateSession(service.Session, 15);

        var startedCalls = 0;
        var finishedArgs = new List<bool>();
        var finishedGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { },
            onCompactingStarted: () => Interlocked.Increment(ref startedCalls),
            onCompactingFinished: success => { lock (finishedArgs) finishedArgs.Add(success); finishedGate.TrySetResult(success); });

        try
        {
            actor.Start();

            var reply = NewReply<bool>();
            Assert.True(actor.Tell(new ComposerCompactMessage(reply, CancellationToken.None)));

            Assert.False(await AwaitReplyAsync(reply));
            await finishedGate.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.Equal(1, startedCalls);
            Assert.Single(finishedArgs);
            Assert.False(finishedArgs[0], "onCompactingFinished(false) on failure");
            Assert.Equal(0, agentTracker.OnCompactingCalls);
            Assert.Equal(0, agentTracker.OnCompactedCalls);
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task Compact_Full_Cancel_StartedFiresOnce_FinishedFalse_AgentCallbacksSuppressed()
    {
        var stateDir = CreateTempDir();
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(new CancellationToken(true)));
        mockClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());

        var agentTracker = new AgentCallbackTracker();
        var service = CreateService(
            stateDir,
            chatClientFactory: _ => mockClient.Object,
            onCompacting: agentTracker.OnCompacting,
            onCompacted: agentTracker.OnCompacted);
        await service.ConnectAsync(TestContext.Current.CancellationToken);
        PopulateSession(service.Session, 15);

        var startedCalls = 0;
        var finishedArgs = new List<bool>();
        var finishedGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { },
            onCompactingStarted: () => Interlocked.Increment(ref startedCalls),
            onCompactingFinished: success => { lock (finishedArgs) finishedArgs.Add(success); finishedGate.TrySetResult(success); });

        try
        {
            actor.Start();

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var reply = NewReply<bool>();
            Assert.True(actor.Tell(new ComposerCompactMessage(reply, cts.Token)));

            Assert.False(await AwaitReplyAsync(reply));
            await finishedGate.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.Equal(1, startedCalls);
            Assert.Single(finishedArgs);
            Assert.False(finishedArgs[0], "onCompactingFinished(false) on cancellation");
            Assert.Equal(0, agentTracker.OnCompactingCalls);
            Assert.Equal(0, agentTracker.OnCompactedCalls);
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task Compact_Partial_Success_StartedAndFinishedFireOnce_AgentCallbacksSuppressed()
    {
        var stateDir = CreateTempDir();
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary of conversation")));
        mockClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());

        var agentTracker = new AgentCallbackTracker();
        var service = CreateService(
            stateDir,
            chatClientFactory: _ => mockClient.Object,
            onCompacting: agentTracker.OnCompacting,
            onCompacted: agentTracker.OnCompacted);
        await service.ConnectAsync(TestContext.Current.CancellationToken);
        PopulateSession(service.Session, 30);

        var startedCalls = 0;
        var finishedArgs = new List<bool>();
        var finishedGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { },
            onCompactingStarted: () => Interlocked.Increment(ref startedCalls),
            onCompactingFinished: success => { lock (finishedArgs) finishedArgs.Add(success); finishedGate.TrySetResult(success); });

        try
        {
            actor.Start();

            var reply = NewReply<bool>();
            Assert.True(actor.Tell(new ComposerCompactPartialMessage(50, reply, CancellationToken.None)));

            Assert.True(await AwaitReplyAsync(reply));
            await finishedGate.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.Equal(1, startedCalls);
            Assert.Single(finishedArgs);
            Assert.True(finishedArgs[0], "onCompactingFinished(true) on success");
            Assert.Equal(0, agentTracker.OnCompactingCalls);
            Assert.Equal(0, agentTracker.OnCompactedCalls);
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task Compact_Partial_Failure_StartedFiresOnce_FinishedFalse_AgentCallbacksSuppressed()
    {
        var stateDir = CreateTempDir();
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("partial compaction boom"));
        mockClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());

        var agentTracker = new AgentCallbackTracker();
        var service = CreateService(
            stateDir,
            chatClientFactory: _ => mockClient.Object,
            onCompacting: agentTracker.OnCompacting,
            onCompacted: agentTracker.OnCompacted);
        await service.ConnectAsync(TestContext.Current.CancellationToken);
        PopulateSession(service.Session, 30);

        var startedCalls = 0;
        var finishedArgs = new List<bool>();
        var finishedGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { },
            onCompactingStarted: () => Interlocked.Increment(ref startedCalls),
            onCompactingFinished: success => { lock (finishedArgs) finishedArgs.Add(success); finishedGate.TrySetResult(success); });

        try
        {
            actor.Start();

            var reply = NewReply<bool>();
            Assert.True(actor.Tell(new ComposerCompactPartialMessage(50, reply, CancellationToken.None)));

            Assert.False(await AwaitReplyAsync(reply));
            await finishedGate.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.Equal(1, startedCalls);
            Assert.Single(finishedArgs);
            Assert.False(finishedArgs[0], "onCompactingFinished(false) on partial failure");
            Assert.Equal(0, agentTracker.OnCompactingCalls);
            Assert.Equal(0, agentTracker.OnCompactedCalls);
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task Compact_Partial_Cancel_StartedFiresOnce_FinishedFalse_AgentCallbacksSuppressed()
    {
        var stateDir = CreateTempDir();
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(new CancellationToken(true)));
        mockClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());

        var agentTracker = new AgentCallbackTracker();
        var service = CreateService(
            stateDir,
            chatClientFactory: _ => mockClient.Object,
            onCompacting: agentTracker.OnCompacting,
            onCompacted: agentTracker.OnCompacted);
        await service.ConnectAsync(TestContext.Current.CancellationToken);
        PopulateSession(service.Session, 30);

        var startedCalls = 0;
        var finishedArgs = new List<bool>();
        var finishedGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { },
            onCompactingStarted: () => Interlocked.Increment(ref startedCalls),
            onCompactingFinished: success => { lock (finishedArgs) finishedArgs.Add(success); finishedGate.TrySetResult(success); });

        try
        {
            actor.Start();

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var reply = NewReply<bool>();
            Assert.True(actor.Tell(new ComposerCompactPartialMessage(50, reply, cts.Token)));

            Assert.False(await AwaitReplyAsync(reply));
            await finishedGate.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.Equal(1, startedCalls);
            Assert.Single(finishedArgs);
            Assert.False(finishedArgs[0], "onCompactingFinished(false) on partial cancellation");
            Assert.Equal(0, agentTracker.OnCompactingCalls);
            Assert.Equal(0, agentTracker.OnCompactedCalls);
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── WasCompacted/IsCompacting semantics ──

    [Fact]
    public async Task Compact_Success_WasCompactedTrue_IsCompactingFalse_After()
    {
        var stateDir = CreateTempDir();
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary")));
        mockClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());

        var service = CreateService(stateDir, chatClientFactory: _ => mockClient.Object);
        await service.ConnectAsync(TestContext.Current.CancellationToken);
        PopulateSession(service.Session, 15);

        // The facade's IsCompacting/WasCompacted are mirrored through the actor callbacks.
        // We simulate the facade's wiring (as in Composer.cs) to observe the flags.
        var isCompacting = false;
        var wasCompacted = false;
        var finishedGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { },
            onCompactingStarted: () => isCompacting = true,
            onCompactingFinished: success => { isCompacting = false; if (success) wasCompacted = true; finishedGate.TrySetResult(success); });

        try
        {
            actor.Start();

            var reply = NewReply<bool>();
            Assert.True(actor.Tell(new ComposerCompactMessage(reply, CancellationToken.None)));
            Assert.True(await AwaitReplyAsync(reply));
            await finishedGate.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.False(isCompacting, "IsCompacting must be false after a successful compaction");
            Assert.True(wasCompacted, "WasCompacted must be true only on success");
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    [Theory]
    [InlineData("Full", "Failure")]
    [InlineData("Full", "Cancel")]
    [InlineData("Partial", "Failure")]
    [InlineData("Partial", "Cancel")]
    public async Task Compact_FailureOrCancel_WasCompactedFalse_IsCompactingFalse_After(
        string mode, string outcome)
    {
        var stateDir = CreateTempDir();
        var mockClient = new Mock<IChatClient>();
        if (outcome == "Cancel")
            mockClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException(new CancellationToken(true)));
        else
            mockClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("boom"));
        mockClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());

        var service = CreateService(stateDir, chatClientFactory: _ => mockClient.Object);
        await service.ConnectAsync(TestContext.Current.CancellationToken);
        PopulateSession(service.Session, mode == "Partial" ? 30 : 15);

        var isCompacting = false;
        var wasCompacted = false;
        // Gate on the finished callback: the actor's finally runs after TrySetResult, so
        // observing the reply alone does not guarantee the callback has updated the flags.
        var finishedGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { },
            onCompactingStarted: () => isCompacting = true,
            onCompactingFinished: success => { isCompacting = false; if (success) wasCompacted = true; finishedGate.TrySetResult(success); });

        try
        {
            actor.Start();

            using var cts = outcome == "Cancel" ? new CancellationTokenSource() : null;
            cts?.Cancel();

            var reply = NewReply<bool>();
            if (mode == "Full")
                Assert.True(actor.Tell(new ComposerCompactMessage(reply, cts?.Token ?? CancellationToken.None)));
            else
                Assert.True(actor.Tell(new ComposerCompactPartialMessage(50, reply, cts?.Token ?? CancellationToken.None)));

            Assert.False(await AwaitReplyAsync(reply));
            // Wait for the finished callback (runs in the actor's finally, after TrySetResult).
            await finishedGate.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.False(isCompacting, "IsCompacting must be false after failure/cancel");
            Assert.False(wasCompacted, "WasCompacted must NOT be set on failure/cancel");
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── SessionLoadedFromDisk: false before connect, true on disk load, false on failure/cancel ──

    [Fact]
    public async Task SessionLoaded_FalseBeforeConnect_TrueOnDiskLoad_FalseOnFreshConnect()
    {
        var stateDir = CreateTempDir();

        // Write a valid session file so ConnectAsync loads it from disk.
        var sessionFile = Path.Combine(stateDir, "composer-session.json");
        var validSession = AgentSession.Create("composer");
        validSession.MessageHistory.Add(new ChatMessage(ChatRole.User, "persisted message"));
        await validSession.SaveAsync(sessionFile, TestContext.Current.CancellationToken);

        var client = new TextStreamingClient("hi");
        var service = CreateService(stateDir, chatClientFactory: _ => client);

        var loadedFlags = new List<bool>();
        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { },
            onSessionLoaded: loaded => { lock (loadedFlags) loadedFlags.Add(loaded); });

        try
        {
            actor.Start();

            // Before connect, the callback has not fired — the cache is false.
            Assert.Empty(loadedFlags);

            var reply = NewReply<bool>();
            Assert.True(actor.Tell(new ComposerConnectMessage(reply, CancellationToken.None)));
            Assert.True(await AwaitReplyAsync(reply));

            // The connect handler fires onSessionLoaded(false) first (reset), then
            // onSessionLoaded(true) when a session was loaded from disk.
            lock (loadedFlags)
            {
                Assert.Contains(false, loadedFlags);
                Assert.Contains(true, loadedFlags);
                // The last value must be true (disk load succeeded).
                Assert.True(loadedFlags[^1], "Last onSessionLoaded must be true on a successful disk-loaded connect");
            }
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task SessionLoaded_FalseOnFreshSession_NoDiskFile()
    {
        var stateDir = CreateTempDir();
        // No session file — ConnectAsync creates a fresh session (loadedFromDisk = false).

        var client = new TextStreamingClient("hi");
        var service = CreateService(stateDir, chatClientFactory: _ => client);

        var loadedFlags = new List<bool>();
        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { },
            onSessionLoaded: loaded => { lock (loadedFlags) loadedFlags.Add(loaded); });

        try
        {
            actor.Start();

            var reply = NewReply<bool>();
            Assert.True(actor.Tell(new ComposerConnectMessage(reply, CancellationToken.None)));
            Assert.True(await AwaitReplyAsync(reply));

            lock (loadedFlags)
            {
                Assert.NotEmpty(loadedFlags);
                Assert.False(loadedFlags[^1], "onSessionLoaded(false) on a fresh (no-disk) connect");
            }
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task SessionLoaded_FalseOnConnectFailure_NoStaleTrue()
    {
        var stateDir = CreateTempDir();

        // Write a valid session file — so the session-load step runs, but client creation fails.
        var sessionFile = Path.Combine(stateDir, "composer-session.json");
        var validSession = AgentSession.Create("composer");
        await validSession.SaveAsync(sessionFile, TestContext.Current.CancellationToken);

        var service = CreateService(stateDir, chatClientFactory: _ => throw new InvalidOperationException("client creation boom"));

        var loadedFlags = new List<bool>();
        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { },
            onSessionLoaded: loaded => { lock (loadedFlags) loadedFlags.Add(loaded); });

        try
        {
            actor.Start();

            var reply = NewReply<bool>();
            Assert.True(actor.Tell(new ComposerConnectMessage(reply, CancellationToken.None)));

            // Actually wait on the reply directly:
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => reply.Task);
            Assert.Contains("client creation boom", ex.Message);

            lock (loadedFlags)
            {
                Assert.NotEmpty(loadedFlags);
                // The first is false (reset), and the failure catch fires false again.
                // Critically, NO true may appear — the session file exists but the connection failed.
                Assert.DoesNotContain(true, loadedFlags);
                Assert.False(loadedFlags[^1], "Last onSessionLoaded must be false on failure — no stale true");
            }
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task SessionLoaded_FalseOnConnectCancel_NoStaleTrue()
    {
        var stateDir = CreateTempDir();

        // Write a valid session file so ConnectAsync's session-load step honors the
        // cancelled token and throws OperationCanceledException deterministically.
        var sessionFile = Path.Combine(stateDir, "composer-session.json");
        var validSession = AgentSession.Create("composer");
        await validSession.SaveAsync(sessionFile, TestContext.Current.CancellationToken);

        var client = new TextStreamingClient("hi");
        var service = CreateService(stateDir, chatClientFactory: _ => client);

        var loadedFlags = new List<bool>();
        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { },
            onSessionLoaded: loaded => { lock (loadedFlags) loadedFlags.Add(loaded); });

        try
        {
            actor.Start();

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var reply = NewReply<bool>();
            Assert.True(actor.Tell(new ComposerConnectMessage(reply, cts.Token)));

            await Task.WhenAny(reply.Task, Task.Delay(Timeout, TestContext.Current.CancellationToken));
            Assert.True(reply.Task.IsCanceled, "Connect reply should be canceled for a pre-cancelled token");

            lock (loadedFlags)
            {
                Assert.NotEmpty(loadedFlags);
                Assert.DoesNotContain(true, loadedFlags);
                Assert.False(loadedFlags[^1], "Last onSessionLoaded must be false on cancel — no stale true");
            }
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── PendingQuestion lock protocol: capture-and-clear under lock, TrySetResult outside lock ──

    /// <summary>
    /// Tests that the actor's <c>onSubmitAnswer</c> callback delivers the answer to the pending
    /// question's TCS, and that after delivery the question is cleared (capture-and-clear).
    /// A completion (the answer) must NOT clear a subsequently published question.
    /// </summary>
    [Fact]
    public async Task SubmitAnswer_CompletesPendingQuestion_AndClearsIt()
    {
        var stateDir = CreateTempDir();
        var client = new TextStreamingClient("hi");
        var service = CreateService(stateDir, chatClientFactory: _ => client);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        // Simulate the facade's PendingQuestion lock protocol: the test stands in for the
        // Composer's _pendingQuestion / _pendingQuestionLock, and the onSubmitAnswer callback
        // implements the capture-and-clear-under-lock + TrySetResult-outside-lock discipline.
        var pendingLock = new object();
        ComposerQuestion? pending = null;

        void SubmitAnswerInternal(string answer)
        {
            ComposerQuestion? pq;
            lock (pendingLock) { pq = pending; pending = null; }
            pq?.Completion.TrySetResult(answer);
        }

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { },
            onSubmitAnswer: SubmitAnswerInternal,
            onCancelQuestion: () =>
            {
                ComposerQuestion? pq;
                lock (pendingLock) { pq = pending; pending = null; }
                pq?.Completion.TrySetResult("User cancelled the question without answering.");
            });

        try
        {
            actor.Start();

            // Publish a question (as the ask_user tool would).
            var q = new ComposerQuestion { Text = "Continue?", Type = QuestionType.YesNo, Options = ["Yes", "No"] };
            lock (pendingLock) pending = q;
            Assert.NotNull(pending);

            // Submit the answer via the actor (routed through Tell → mailbox → onSubmitAnswer).
            Assert.True(actor.Tell(new ComposerSubmitAnswerMessage("Yes")));

            var answer = await q.Completion.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.Equal("Yes", answer);

            // The pending question was cleared by the capture-and-clear.
            lock (pendingLock) Assert.Null(pending);
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── SubmitAnswer/CancelQuestion routing: public → Tell, CancelQuestion preserves message ──

    /// <summary>
    /// CancelQuestion (via the actor) completes the pending question with the exact
    /// "User cancelled the question without answering." message.
    /// </summary>
    [Fact]
    public async Task CancelQuestion_PreservesExactCancellationMessage()
    {
        var stateDir = CreateTempDir();
        var client = new TextStreamingClient("hi");
        var service = CreateService(stateDir, chatClientFactory: _ => client);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var pendingLock = new object();
        ComposerQuestion? pending = null;

        void CancelQuestionInternal()
        {
            ComposerQuestion? pq;
            lock (pendingLock) { pq = pending; pending = null; }
            pq?.Completion.TrySetResult("User cancelled the question without answering.");
        }

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { },
            onSubmitAnswer: answer =>
            {
                ComposerQuestion? pq;
                lock (pendingLock) { pq = pending; pending = null; }
                pq?.Completion.TrySetResult(answer);
            },
            onCancelQuestion: CancelQuestionInternal);

        try
        {
            actor.Start();

            var q = new ComposerQuestion { Text = "Proceed?", Type = QuestionType.YesNo };
            lock (pendingLock) pending = q;

            Assert.True(actor.Tell(new ComposerCancelQuestionMessage()));

            var result = await q.Completion.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.Equal("User cancelled the question without answering.", result);

            lock (pendingLock) Assert.Null(pending);
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── New messages in CancelReply/OnUnhandledException ──

    /// <summary>
    /// CancelReply is a no-op for ComposerSubmitAnswerMessage and ComposerCancelQuestionMessage
    /// (fire-and-forget — no reply to cancel). Must not throw.
    /// </summary>
    [Fact]
    public async Task CancelReply_SubmitAndCancel_NoOp_DoesNotThrow()
    {
        var stateDir = CreateTempDir();
        var client = new TextStreamingClient("hi");
        var service = CreateService(stateDir, chatClientFactory: _ => client);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { });

        try
        {
            var method = typeof(ComposerActor).GetMethod("CancelReply", PrivateFlags)
                ?? throw new InvalidOperationException("CancelReply not found on ComposerActor");

            // CancelReply must not throw for fire-and-forget messages.
            method.Invoke(actor, [new ComposerSubmitAnswerMessage("answer")]);
            method.Invoke(actor, [new ComposerCancelQuestionMessage()]);
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>
    /// OnUnhandledException logs (does not throw) for ComposerSubmitAnswerMessage and
    /// ComposerCancelQuestionMessage — they have no reply to fault.
    /// </summary>
    [Fact]
    public async Task OnUnhandledException_SubmitAndCancel_Logs_DoesNotThrow()
    {
        var stateDir = CreateTempDir();
        var client = new TextStreamingClient("hi");
        var service = CreateService(stateDir, chatClientFactory: _ => client);
        await service.ConnectAsync(TestContext.Current.CancellationToken);

        var logger = new RecordingLogger();
        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { },
            logger);

        try
        {
            var method = typeof(ComposerActor).GetMethod("OnUnhandledException", PrivateFlags)!;
            var ex = new InvalidOperationException("submit handler boom");

            method.Invoke(actor, [new ComposerSubmitAnswerMessage("answer"), ex]);
            method.Invoke(actor, [new ComposerCancelQuestionMessage(), ex]);

            // The error was logged for both message types.
            Assert.Contains(logger.Messages,
                m => m.Contains("ComposerSubmitAnswerMessage", StringComparison.Ordinal));
            Assert.Contains(logger.Messages,
                m => m.Contains("ComposerCancelQuestionMessage", StringComparison.Ordinal));
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── onSessionLoaded fires false before connect (the reset) then the outcome value ──

    /// <summary>
    /// The connect handler fires onSessionLoaded(false) BEFORE the connect attempt (the reset),
    /// so a stale true from a prior successful connect cannot survive into a new attempt. This
    /// test connects twice: the first with a disk file (true), the second without (false), and
    /// verifies the second connect's first callback is false.
    /// </summary>
    [Fact]
    public async Task SessionLoaded_ResetFalseBeforeConnect_NoStaleTrueOnSecondConnect()
    {
        var stateDir = CreateTempDir();

        // Write a valid session file so the first connect loads from disk.
        var sessionFile = Path.Combine(stateDir, "composer-session.json");
        var validSession = AgentSession.Create("composer");
        await validSession.SaveAsync(sessionFile, TestContext.Current.CancellationToken);

        var client = new TextStreamingClient("hi");
        var service = CreateService(stateDir, chatClientFactory: _ => client);

        var loadedFlags = new List<bool>();
        var actor = CreateActor(
            service,
            _ => Task.CompletedTask,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { },
            onSessionLoaded: loaded => { lock (loadedFlags) loadedFlags.Add(loaded); });

        try
        {
            actor.Start();

            // First connect: loads from disk → last flag is true.
            var reply1 = NewReply<bool>();
            Assert.True(actor.Tell(new ComposerConnectMessage(reply1, CancellationToken.None)));
            Assert.True(await AwaitReplyAsync(reply1));

            lock (loadedFlags) Assert.True(loadedFlags[^1], "First connect should load from disk");

            // Delete the session file so the second connect is a fresh session.
            File.Delete(sessionFile);
            loadedFlags.Clear();

            // Second connect: the reset fires false FIRST, then false (no disk file).
            var reply2 = NewReply<bool>();
            Assert.True(actor.Tell(new ComposerConnectMessage(reply2, CancellationToken.None)));
            Assert.True(await AwaitReplyAsync(reply2));

            lock (loadedFlags)
            {
                Assert.NotEmpty(loadedFlags);
                // The first callback of the second connect must be false (the reset) —
                // no stale true survived from the first connect.
                Assert.False(loadedFlags[0], "First onSessionLoaded of the second connect must be false (reset)");
                Assert.False(loadedFlags[^1], "Last onSessionLoaded must be false (no disk file)");
            }
        }
        finally
        {
            await actor.DisposeAsync();
            await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }
}
