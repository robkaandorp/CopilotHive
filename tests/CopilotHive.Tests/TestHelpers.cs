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
    /// <remarks>
    /// <para>
    /// Retries on a BOUNDED, generous schedule: the first delete attempt is immediate, then up to
    /// eight further attempts follow linear waits of 100, 200, … 800 ms — 3.6 s of total wait across
    /// nine delete attempts. This is bounded handle-release retry, NOT elapsed-time flakiness
    /// masking: the only waiters this exists for are processes the fixture itself just killed (e.g.
    /// the mid-merge git process tree in <c>BrainRepoManagerReleaseOpsTests</c>), and Windows can
    /// hold their file handles for a short while after the process dies, so the delete can
    /// transiently fail with <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>.
    /// The budget is fixed — there is no polling loop that grows until a flaky condition happens to
    /// pass.
    /// </para>
    /// <para>
    /// Fails LOUDLY: if the directory still exists after the final attempt, an
    /// <see cref="InvalidOperationException"/> naming the full path and carrying the last delete
    /// exception is thrown. Residue is never left silently, because a silent failure only moves the
    /// symptom to an unrelated later test.
    /// </para>
    /// <para>
    /// <paramref name="deleteOverride"/> and <paramref name="waitOverride"/> are TEST SEAMS: they let
    /// a test drive the retry/loud-failure contract deterministically (a real locked directory
    /// cannot be produced on every platform, and a real retry schedule would make the test depend
    /// on elapsed time). When both are <c>null</c> — the production path used by every fixture —
    /// the behaviour is exactly the real recursive delete with a real bounded sleep.
    /// </para>
    /// </remarks>
    /// <param name="path">Directory to delete.</param>
    /// <param name="maxAttempts">Total number of delete attempts, including the immediate first one.</param>
    /// <param name="deleteOverride">Test seam replacing the recursive delete; receives the path.</param>
    /// <param name="waitOverride">Test seam replacing the backoff sleep; receives the delay in milliseconds.</param>
    internal static void ForceDeleteDirectory(
        string path,
        int maxAttempts = 9,
        Action<string>? deleteOverride = null,
        Action<int>? waitOverride = null)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (!Directory.Exists(path))
                return;

            try
            {
                ClearReadOnlyAttributes(path);
                if (deleteOverride is not null)
                    deleteOverride(path);
                else
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;

                // Linear backoff BETWEEN attempts only: attempt 1 is immediate, and the final
                // attempt's failure is reported rather than slept on.
                if (attempt < maxAttempts)
                {
                    var delayMs = 100 * attempt;
                    if (waitOverride is not null)
                        waitOverride(delayMs);
                    else
                        Thread.Sleep(delayMs);
                }
            }
        }

        if (!Directory.Exists(path))
            return;

        throw new InvalidOperationException(
            $"Failed to delete the temporary directory '{Path.GetFullPath(path)}' after {maxAttempts} "
            + "attempts — a killed process is presumably still holding its file handles. The last "
            + "delete error is attached as the inner exception.",
            lastError);
    }

    /// <summary>
    /// Resets every file under <paramref name="path"/> to <see cref="FileAttributes.Normal"/> so a
    /// checkout with read-only git objects (the Windows git default for pack files) can be deleted.
    /// </summary>
    private static void ClearReadOnlyAttributes(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
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
