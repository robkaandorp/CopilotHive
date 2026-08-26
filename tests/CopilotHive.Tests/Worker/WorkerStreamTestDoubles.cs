using Grpc.Core;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Base class for <see cref="IClientStreamWriter{T}"/> test doubles. Implements the
/// two-argument <c>WriteAsync(T, CancellationToken)</c> overload explicitly: the default
/// interface method on <see cref="IAsyncStreamWriter{T}"/> throws
/// <see cref="NotSupportedException"/> for any cancellable token, which would land on an
/// unobserved TCS and hang the test host.
/// </summary>
internal abstract class FakeClientStreamWriter<T> : IClientStreamWriter<T>
{
    public WriteOptions? WriteOptions { get; set; }

    public abstract Task WriteAsync(T message);

    Task IAsyncStreamWriter<T>.WriteAsync(T message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return WriteAsync(message);
    }

    public abstract Task CompleteAsync();
}
