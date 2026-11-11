using Microsoft.Extensions.AI;
using Moq;

namespace CopilotHive.Tests.Services;

/// <summary>
/// A real runtime client whose async disposal is awaited by Composer and its manager.
/// The one-shot hold yields at an await: no manager hook, lock, or worker thread blocks.
/// </summary>
internal sealed class RuntimeAsyncDisposalHold : IChatClient, IAsyncDisposable
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _armed;
    private int _timedOut;
    private readonly IChatClient _inner = new Mock<IChatClient>().Object;

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Arm() => Interlocked.Exchange(ref _armed, 1);
    public void Release() => _release.TrySetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _armed, 0) == 0) return;
        Entered.TrySetResult();
        try { await _release.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None); }
        catch (TimeoutException) { Interlocked.Exchange(ref _timedOut, 1); throw; }
    }

    public void AssertHealthy() => Assert.True(Volatile.Read(ref _timedOut) == 0,
        "Timed out: release the held async client disposal");
    public void Dispose() => throw new InvalidOperationException("The async disposal path must be used.");
    public object? GetService(Type type, object? key = null) => _inner.GetService(type, key);
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        _inner.GetResponseAsync(messages, options, cancellationToken);
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        _inner.GetStreamingResponseAsync(messages, options, cancellationToken);
}

