using CopilotHive.Worker;

using Microsoft.Extensions.AI;

using System.Reflection;
using System.Runtime.CompilerServices;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Tests for the lazy LLM client creation and fallible disposal in <see cref="SharpCoderRunner"/>.
/// <para>
/// Covers: <see cref="SharpCoderRunner.ConnectAsync"/> and
/// <see cref="SharpCoderRunner.ResetSessionAsync"/> create NO client;
/// <see cref="SharpCoderRunner.SendPromptAsync"/> creates it lazily on the first prompt;
/// <see cref="SharpCoderRunner.ResetSessionAsync"/> disposes + nulls the prior client
/// deterministically (null-then-dispose so a throwing dispose cannot leave a half-set field);
/// disposal is idempotent; a disposal exception propagates; a subsequent
/// <see cref="SharpCoderRunner.SendPromptAsync"/> re-creates the client cleanly.
/// </para>
/// </summary>
public sealed class SharpCoderRunnerLazyClientTests
{
    // ── Reflection helpers ─────────────────────────────────────────────────────

    private static readonly FieldInfo ChatClientField =
        typeof(SharpCoderRunner).GetField("_chatClient", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("_chatClient field not found.");

    private static readonly FieldInfo PendingModelField =
        typeof(SharpCoderRunner).GetField("_pendingModel", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("_pendingModel field not found.");

    private static IChatClient? GetChatClient(SharpCoderRunner runner) =>
        (IChatClient?)ChatClientField.GetValue(runner);

    private static string? GetPendingModel(SharpCoderRunner runner) =>
        (string?)PendingModelField.GetValue(runner);

    // ── Stub chat client ───────────────────────────────────────────────────────

    /// <summary>
    /// A stub client that returns a single assistant message and tracks disposal.
    /// </summary>
    private sealed class StubClient : IChatClient
    {
        internal int DisposeCount;
        internal bool WasDisposed => DisposeCount > 0;

        public ChatClientMetadata Metadata => new("stub", null, "stub-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."))
            {
                FinishReason = ChatFinishReason.Stop,
            };
            return Task.FromResult(resp);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => StreamAsync(ct);

        private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Done.")]);
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop, Role = ChatRole.Assistant };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    /// <summary>A client whose Dispose throws — used to test null-then-dispose semantics.</summary>
    private sealed class ThrowingDisposeClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("throw", null, "throw-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."))
            {
                FinishReason = ChatFinishReason.Stop,
            };
            return Task.FromResult(resp);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => StreamAsync(ct);

        private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Done.")]);
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop, Role = ChatRole.Assistant };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => throw new InvalidOperationException("dispose boom");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string CreateTempWorkDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lazy-client-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    // ===========================================================================
    // Lazy client creation: ConnectAsync and ResetSessionAsync create no client
    // ===========================================================================

    [Fact]
    public async Task ConnectAsync_CreatesNoClient()
    {
        // Use the parameterless constructor (production path) — no client injected
        var runner = new SharpCoderRunner("/config-repo");

        await runner.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Null(GetChatClient(runner));
    }

    [Fact]
    public async Task ResetSessionAsync_CreatesNoClient()
    {
        var runner = new SharpCoderRunner("/config-repo");

        await runner.ResetSessionAsync("test-model", null, TestContext.Current.CancellationToken);

        Assert.Null(GetChatClient(runner));
    }

    [Fact]
    public async Task ResetSessionAsync_RecordsPendingModel()
    {
        var runner = new SharpCoderRunner("/config-repo");

        await runner.ResetSessionAsync("my-model", null, TestContext.Current.CancellationToken);

        Assert.Equal("my-model", GetPendingModel(runner));
    }

    [Fact]
    public async Task ResetSessionAsync_NullModel_PendingModelIsNull()
    {
        var runner = new SharpCoderRunner("/config-repo");

        await runner.ResetSessionAsync(null, null, TestContext.Current.CancellationToken);

        Assert.Null(GetPendingModel(runner));
    }

    // ===========================================================================
    // SendPromptAsync creates the client lazily on the first prompt
    // ===========================================================================

    [Fact]
    public async Task SendPromptAsync_CreatesClientLazily_OnFirstPrompt()
    {
        var workDir = CreateTempWorkDir();
        try
        {
            var stub = new StubClient();
            var runner = new SharpCoderRunner("/config-repo");
            runner.ClientCreationSeam = _ => stub;

            // Before SendPromptAsync, no client
            Assert.Null(GetChatClient(runner));

            await runner.SendPromptAsync("do something", workDir, TestContext.Current.CancellationToken);

            // After SendPromptAsync, the client was created
            Assert.NotNull(GetChatClient(runner));
            Assert.Same(stub, GetChatClient(runner));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task SendPromptAsync_AfterReset_CreatesFreshClient()
    {
        var workDir = CreateTempWorkDir();
        try
        {
            var firstStub = new StubClient();
            var secondStub = new StubClient();
            var runner = new SharpCoderRunner("/config-repo");

            // First prompt: creates first client
            runner.ClientCreationSeam = _ => firstStub;
            await runner.SendPromptAsync("first prompt", workDir, TestContext.Current.CancellationToken);
            Assert.Same(firstStub, GetChatClient(runner));

            // Reset: disposes + nulls the client
            await runner.ResetSessionAsync("new-model", null, TestContext.Current.CancellationToken);
            Assert.Null(GetChatClient(runner));
            Assert.True(firstStub.WasDisposed);

            // Second prompt: creates a fresh client
            runner.ClientCreationSeam = _ => secondStub;
            await runner.SendPromptAsync("second prompt", workDir, TestContext.Current.CancellationToken);
            Assert.Same(secondStub, GetChatClient(runner));
            Assert.NotSame(firstStub, GetChatClient(runner));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    // ===========================================================================
    // Fallible disposal: null-then-dispose, idempotent, throw propagation, clean re-creation
    // ===========================================================================

    [Fact]
    public async Task ResetSessionAsync_NullThenDispose_ThrowingDisposeLeavesFieldNull()
    {
        var throwingClient = new ThrowingDisposeClient();
        var runner = new SharpCoderRunner("/config-repo");
        runner.ClientCreationSeam = _ => throwingClient;

        // Get a client into the field via SendPromptAsync
        var workDir = CreateTempWorkDir();
        try
        {
            await runner.SendPromptAsync("prompt", workDir, TestContext.Current.CancellationToken);
            Assert.NotNull(GetChatClient(runner));

            // Reset: null-then-dispose. The dispose throws, but the field must already be null.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runner.ResetSessionAsync("new-model", null, TestContext.Current.CancellationToken));

            // The field is null even though dispose threw — no half-set client
            Assert.Null(GetChatClient(runner));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task ResetSessionAsync_DisposalIsIdempotent()
    {
        var stub = new StubClient();
        var runner = new SharpCoderRunner("/config-repo");
        runner.ClientCreationSeam = _ => stub;

        var workDir = CreateTempWorkDir();
        try
        {
            await runner.SendPromptAsync("prompt", workDir, TestContext.Current.CancellationToken);
            Assert.Same(stub, GetChatClient(runner));

            // First reset: disposes + nulls
            await runner.ResetSessionAsync(null, null, TestContext.Current.CancellationToken);
            Assert.Null(GetChatClient(runner));
            Assert.Equal(1, stub.DisposeCount);

            // Second reset: nothing to dispose, no throw, no extra dispose call
            await runner.ResetSessionAsync(null, null, TestContext.Current.CancellationToken);
            Assert.Null(GetChatClient(runner));
            // Dispose was called only once (the first reset)
            Assert.Equal(1, stub.DisposeCount);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task ResetSessionAsync_DisposalException_Propagates()
    {
        var throwingClient = new ThrowingDisposeClient();
        var runner = new SharpCoderRunner("/config-repo");
        runner.ClientCreationSeam = _ => throwingClient;

        var workDir = CreateTempWorkDir();
        try
        {
            await runner.SendPromptAsync("prompt", workDir, TestContext.Current.CancellationToken);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runner.ResetSessionAsync(null, null, TestContext.Current.CancellationToken));
            Assert.Contains("dispose boom", ex.Message);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task SendPromptAsync_AfterDisposalThrow_RecreatesClientCleanly()
    {
        var workDir = CreateTempWorkDir();
        try
        {
            var throwingClient = new ThrowingDisposeClient();
            var cleanClient = new StubClient();
            var runner = new SharpCoderRunner("/config-repo");
            runner.ClientCreationSeam = _ => throwingClient;

            // First prompt: creates the throwing client
            await runner.SendPromptAsync("prompt", workDir, TestContext.Current.CancellationToken);
            Assert.Same(throwingClient, GetChatClient(runner));

            // Reset: dispose throws, field is nulled
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runner.ResetSessionAsync("new-model", null, TestContext.Current.CancellationToken));
            Assert.Null(GetChatClient(runner));

            // Switch the seam to a clean client and send again — must re-create cleanly
            runner.ClientCreationSeam = _ => cleanClient;
            await runner.SendPromptAsync("second prompt", workDir, TestContext.Current.CancellationToken);

            Assert.Same(cleanClient, GetChatClient(runner));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    // ===========================================================================
    // DisposeAsync: null-then-dispose + idempotent
    // ===========================================================================

    [Fact]
    public async Task DisposeAsync_NullThenDispose_ThrowingDisposeLeavesFieldNull()
    {
        var throwingClient = new ThrowingDisposeClient();
        var runner = new SharpCoderRunner("/config-repo");
        runner.ClientCreationSeam = _ => throwingClient;

        var workDir = CreateTempWorkDir();
        try
        {
            await runner.SendPromptAsync("prompt", workDir, TestContext.Current.CancellationToken);
            Assert.NotNull(GetChatClient(runner));

            // DisposeAsync: null-then-dispose. The dispose throws.
            // Note: DisposeAsync returns ValueTask.CompletedTask AFTER calling Dispose,
            // so the exception propagates synchronously through the Dispose() call.
            Assert.Throws<InvalidOperationException>(() => runner.DisposeAsync().AsTask().GetAwaiter().GetResult());

            // The field is null even though dispose threw
            Assert.Null(GetChatClient(runner));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task DisposeAsync_Idempotent()
    {
        var stub = new StubClient();
        var runner = new SharpCoderRunner("/config-repo");
        runner.ClientCreationSeam = _ => stub;

        var workDir = CreateTempWorkDir();
        try
        {
            await runner.SendPromptAsync("prompt", workDir, TestContext.Current.CancellationToken);

            await runner.DisposeAsync();
            Assert.Equal(1, stub.DisposeCount);

            // Second dispose: nothing to dispose
            await runner.DisposeAsync();
            Assert.Equal(1, stub.DisposeCount);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task DisposeAsync_WhenNoClient_NoThrow()
    {
        var runner = new SharpCoderRunner("/config-repo");

        // No client was ever created
        await runner.DisposeAsync();
        Assert.Null(GetChatClient(runner));
    }

    // ===========================================================================
    // Config provisioner integration: SendPromptAsync invokes provisioner before client creation
    // ===========================================================================

    [Fact]
    public async Task SendPromptAsync_InvokesProvisionerBeforeClientCreation()
    {
        var workDir = CreateTempWorkDir();
        try
        {
            var stub = new StubClient();
            var runner = new SharpCoderRunner("/config-repo");

            var provisionerCalls = 0;
            string? capturedModel = null;
            runner.SetConfigProvisioner((model, ct) =>
            {
                Interlocked.Increment(ref provisionerCalls);
                capturedModel = model;
                return Task.CompletedTask;
            });
            runner.ClientCreationSeam = _ =>
            {
                // The provisioner must have run BEFORE the client is created
                Assert.True(Volatile.Read(ref provisionerCalls) > 0,
                    "Provisioner must run before client creation");
                return stub;
            };

            await runner.ResetSessionAsync("task-model-xyz", null, TestContext.Current.CancellationToken);
            await runner.SendPromptAsync("prompt", workDir, TestContext.Current.CancellationToken);

            Assert.Equal(1, provisionerCalls);
            Assert.Equal("task-model-xyz", capturedModel);
            Assert.Same(stub, GetChatClient(runner));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task SendPromptAsync_ProvisionerNull_CreatesClientDirectly()
    {
        var workDir = CreateTempWorkDir();
        try
        {
            var stub = new StubClient();
            var runner = new SharpCoderRunner("/config-repo");
            // No provisioner set (null) — client created directly
            runner.ClientCreationSeam = _ => stub;

            await runner.SendPromptAsync("prompt", workDir, TestContext.Current.CancellationToken);

            Assert.Same(stub, GetChatClient(runner));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}