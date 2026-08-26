using CopilotHive.Worker;
using CopilotHive.Workers;

using Microsoft.Extensions.AI;

using System.Runtime.CompilerServices;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Proves the client lifecycle lease spans ACTUAL USE, not merely acquisition.
/// <para>
/// The rejected revision released <c>_clientLifecycleGate</c> inside
/// <c>AcquireClientAsync</c> before <c>SendPromptAsync</c> ever touched the returned reference,
/// so <c>ResetSessionAsync</c>/<c>DisposeAsync</c> could null and dispose the client while a turn
/// was still running against it. These tests gate a turn open with a
/// <see cref="TaskCompletionSource"/> — never a timing delay — and assert that a concurrent
/// reset/dispose cannot complete, and cannot dispose the in-use client, until the turn finishes.
/// </para>
/// </summary>
public sealed class SharpCoderRunnerLeaseSpanTests
{
    private static string CreateWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lease-span-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// While a turn is in flight, <c>ResetSessionAsync</c> must BLOCK on the lifecycle gate. If
    /// the gate were released at acquisition (the rejected shape), the reset would complete
    /// immediately and dispose the client the turn is still using.
    /// </summary>
    [Fact]
    public async Task ResetSessionAsync_BlocksWhileTurnInFlight_AndDoesNotDisposeInUseClient()
    {
        var workDir = CreateWorkDir();
        var runner = new SharpCoderRunner();
        var turnEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new GatedStubChatClient(turnEntered, releaseTurn);

        try
        {
            runner.ClientCreationSeam = _ => client;
            runner.SetCustomAgent(WorkerRole.Coder, "coder");

            var turn = runner.SendPromptAsync("work", workDir, TestContext.Current.CancellationToken);

            // The turn is now inside the agent run, holding the lease.
            await turnEntered.Task;

            var reset = runner.ResetSessionAsync("other-model", null, CancellationToken.None);

            // The lease spans use, so the reset cannot have completed.
            Assert.False(reset.IsCompleted, "ResetSessionAsync must block while a turn holds the client lease.");

            // And critically: the in-use client has NOT been disposed underneath the turn.
            Assert.Equal(0, client.DisposeCallCount);

            // Let the turn finish; only then may the reset proceed.
            releaseTurn.SetResult();
            await turn;
            await reset;

            // The reset disposed the client only after the turn released the lease.
            Assert.Equal(1, client.DisposeCallCount);
        }
        finally
        {
            releaseTurn.TrySetResult();
            await runner.DisposeAsync();
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// The same guarantee for final teardown: <c>DisposeAsync</c> must not dispose a client that
    /// a turn is still using.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_BlocksWhileTurnInFlight_AndDoesNotDisposeInUseClient()
    {
        var workDir = CreateWorkDir();
        var runner = new SharpCoderRunner();
        var turnEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new GatedStubChatClient(turnEntered, releaseTurn);

        try
        {
            runner.ClientCreationSeam = _ => client;
            runner.SetCustomAgent(WorkerRole.Coder, "coder");

            var turn = runner.SendPromptAsync("work", workDir, TestContext.Current.CancellationToken);
            await turnEntered.Task;

            var dispose = runner.DisposeAsync().AsTask();

            Assert.False(dispose.IsCompleted, "DisposeAsync must block while a turn holds the client lease.");
            Assert.Equal(0, client.DisposeCallCount);

            releaseTurn.SetResult();
            await turn;
            await dispose;

            Assert.Equal(1, client.DisposeCallCount);
        }
        finally
        {
            releaseTurn.TrySetResult();
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// The lease must be released even when the turn's underlying LLM call FAILS, or a later
    /// reset would hang forever. The agent converts a streaming fault into a non-success result
    /// rather than rethrowing, so this asserts the lease outcome, not the exception shape.
    /// </summary>
    [Fact]
    public async Task FailedTurn_StillReleasesLease()
    {
        var workDir = CreateWorkDir();
        var runner = new SharpCoderRunner();
        var client = new ThrowingStubChatClient();

        try
        {
            runner.ClientCreationSeam = _ => client;
            runner.SetCustomAgent(WorkerRole.Coder, "coder");

            // Whether the agent surfaces the fault as an exception or a non-success result is not
            // this test's concern — either way the lease must come back.
            try
            {
                await runner.SendPromptAsync("work", workDir, TestContext.Current.CancellationToken);
            }
            catch (Exception)
            {
                // Acceptable: the turn may propagate instead of returning a failed result.
            }

            // The lease was released by the finally, so this completes rather than deadlocking.
            var reset = runner.ResetSessionAsync("next-model", null, TestContext.Current.CancellationToken);
            await reset;
            Assert.True(reset.IsCompletedSuccessfully);
        }
        finally
        {
            await runner.DisposeAsync();
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// A failure during lazy CREATION must also release the lease — the caller never receives a
    /// reference, so it has no <c>finally</c> of its own to run.
    /// </summary>
    [Fact]
    public async Task FailedClientCreation_StillReleasesLease()
    {
        var workDir = CreateWorkDir();
        var runner = new SharpCoderRunner();

        try
        {
            runner.ClientCreationSeam = _ => throw new InvalidOperationException("creation failed");
            runner.SetCustomAgent(WorkerRole.Coder, "coder");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => runner.SendPromptAsync("work", workDir, TestContext.Current.CancellationToken));

            // Not deadlocked: the lease was released on the creation-failure path.
            await runner.ResetSessionAsync("next-model", null, TestContext.Current.CancellationToken);
        }
        finally
        {
            await runner.DisposeAsync();
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// Two concurrent turns must be serialized by the lease and must share ONE client — never
    /// construct two.
    /// </summary>
    [Fact]
    public async Task ConcurrentTurns_ShareASingleClient()
    {
        var workDir = CreateWorkDir();
        var runner = new SharpCoderRunner();
        var created = 0;

        try
        {
            runner.ClientCreationSeam = _ =>
            {
                Interlocked.Increment(ref created);
                return new ImmediateStubChatClient();
            };
            runner.SetCustomAgent(WorkerRole.Coder, "coder");

            var a = runner.SendPromptAsync("a", workDir, TestContext.Current.CancellationToken);
            var b = runner.SendPromptAsync("b", workDir, TestContext.Current.CancellationToken);
            await Task.WhenAll(a, b);

            Assert.Equal(1, Volatile.Read(ref created));
        }
        finally
        {
            await runner.DisposeAsync();
            Directory.Delete(workDir, recursive: true);
        }
    }
}

// ── Stub chat clients ────────────────────────────────────────────────────────

/// <summary>
/// Signals when a turn has entered the streaming call and blocks there until released, so a test
/// can deterministically observe the window in which the lease must be held.
/// </summary>
file sealed class GatedStubChatClient(TaskCompletionSource entered, TaskCompletionSource release) : IChatClient
{
    private int _disposeCallCount;

    public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."))
        {
            FinishReason = ChatFinishReason.Stop,
        });

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => StreamAsync(cancellationToken);

    private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        entered.TrySetResult();
        await release.Task.WaitAsync(cancellationToken);

        yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Done.")]);
        yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop, Role = ChatRole.Assistant };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() => Interlocked.Increment(ref _disposeCallCount);
}

/// <summary>Fails the streaming call so the turn throws.</summary>
file sealed class ThrowingStubChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("turn failed");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => StreamAsync();

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync()
    {
        await Task.Yield();
        throw new InvalidOperationException("turn failed");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>Completes immediately so concurrent turns can be serialized without gating.</summary>
file sealed class ImmediateStubChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."))
        {
            FinishReason = ChatFinishReason.Stop,
        });

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => StreamAsync();

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync()
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Done.")]);
        yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop, Role = ChatRole.Assistant };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
