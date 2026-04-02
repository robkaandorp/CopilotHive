namespace CopilotHive.Tests;

using Microsoft.Extensions.AI;

/// <summary>Shared test utilities.</summary>
internal static class TestHelpers
{
    /// <summary>
    /// Recursively deletes a directory, clearing read-only attributes first so that
    /// <c>.git</c> pack-files and other locked objects can be removed on Windows.
    /// </summary>
    internal static void ForceDeleteDirectory(string path, int maxRetries = 3)
    {
        for (var i = 0; i < maxRetries; i++)
        {
            if (!Directory.Exists(path))
                return;

            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException) when (i < maxRetries - 1)
            {
                Thread.Sleep(200 * (i + 1));
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                Thread.Sleep(200 * (i + 1));
            }
        }
    }
}

/// <summary>
/// Minimal IChatClient stub that returns empty text responses.
/// Used in tests that need DistributedBrain to be connected but don't exercise LLM calls.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    public ChatClientMetadata Metadata => new("fake", null, "fake-model");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "")));

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Streaming not used in fake client.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
