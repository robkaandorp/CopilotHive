namespace CopilotHive.Tests;

using CopilotHive.Configuration;
using CopilotHive.Workers;
using Microsoft.Extensions.AI;

/// <summary>Shared test utilities.</summary>
internal static class TestHelpers
{
    /// <summary>
    /// Returns a <see cref="HiveConfigFile.Workers"/> dictionary with a configured model
    /// for EVERY broadcastable role (Coder, Tester, Reviewer, Improver, DocWriter) plus
    /// the Brain (Orchestrator.Model). This satisfies the all-or-nothing readiness gate in
    /// <see cref="CopilotHive.Services.GoalDispatchService"/> so dispatch proceeds as today.
    /// MergeWorker is deliberately omitted — it is NOT broadcastable and must not block dispatch.
    /// </summary>
    internal static Dictionary<string, WorkerConfig> AllBroadcastableRoleModels(string modelPrefix = "test") =>
        new()
        {
            ["coder"] = new WorkerConfig { Model = $"{modelPrefix}-coder-model" },
            ["tester"] = new WorkerConfig { Model = $"{modelPrefix}-tester-model" },
            ["reviewer"] = new WorkerConfig { Model = $"{modelPrefix}-reviewer-model" },
            ["improver"] = new WorkerConfig { Model = $"{modelPrefix}-improver-model" },
            ["docwriter"] = new WorkerConfig { Model = $"{modelPrefix}-docwriter-model" },
        };

    /// <summary>
    /// Returns a <see cref="HiveConfigFile"/> with the Brain model
    /// (<see cref="OrchestratorConfig.Model"/>) and all broadcastable role models configured,
    /// so the readiness gate passes and dispatch proceeds as today.
    /// </summary>
    internal static HiveConfigFile FullReadyConfig(string modelPrefix = "test") =>
        new()
        {
            Orchestrator = new OrchestratorConfig { Model = $"{modelPrefix}-brain-model" },
            Workers = AllBroadcastableRoleModels(modelPrefix),
        };
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

/// <summary>
/// <see cref="ConfigRepoManager"/> whose <see cref="ConfigRepoManager.DeleteFileAsync"/>
/// always throws <see cref="OperationCanceledException"/>. Used to verify that knowledge
/// document cleanup failures are best-effort and never fail goal deletion.
/// </summary>
internal sealed class ThrowingConfigRepoManager : ConfigRepoManager
{
    public ThrowingConfigRepoManager(string localPath)
        : base("http://localhost/invalid-config-repo", localPath)
    {
    }

    public override Task DeleteFileAsync(string filePath, string commitMessage, CancellationToken ct = default)
        => throw new OperationCanceledException("Simulated knowledge document cleanup failure");
}
