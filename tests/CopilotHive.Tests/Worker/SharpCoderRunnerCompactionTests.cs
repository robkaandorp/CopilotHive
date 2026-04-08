extern alias WorkerAssembly;

using System.Reflection;
using System.Runtime.CompilerServices;
using CopilotHive.Workers;
using Microsoft.Extensions.AI;
using SharpCoder;
using WorkerAssembly::CopilotHive.Worker;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Unit tests for <see cref="SharpCoderRunner.SetCompactionModel"/>:
/// verifies that the private <c>_compactionModel</c> field is stored correctly
/// and that the null path does not interfere with <see cref="SharpCoderRunner.SendPromptAsync"/>.
/// </summary>
public sealed class SharpCoderRunnerCompactionTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SharpCoderRunner CreateRunner()
        => new(new StubChatClientForCompaction(), "test-model");

    private static readonly FieldInfo CompactionModelField =
        typeof(SharpCoderRunner).GetField("_compactionModel", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("_compactionModel field not found on SharpCoderRunner.");

    // ── Test D: SetCompactionModel(null) field stores null ─────────────────────

    [Fact]
    public void SetCompactionModel_Null_FieldIsNull()
    {
        var runner = CreateRunner();

        // Before any call, the field should be null
        Assert.Null(CompactionModelField.GetValue(runner));

        runner.SetCompactionModel(null);

        Assert.Null(CompactionModelField.GetValue(runner));
    }

    // ── Test E: SetCompactionModel(value) field stores the value ────────────────

    [Fact]
    public void SetCompactionModel_NonNull_FieldStoresValue()
    {
        var runner = CreateRunner();

        runner.SetCompactionModel("copilot/gpt-5.4-mini");

        Assert.Equal("copilot/gpt-5.4-mini", CompactionModelField.GetValue(runner));
    }

    // ── Test F: Multiple SetCompactionModel calls keep the last value ──────────

    [Fact]
    public void SetCompactionModel_CalledMultipleTimes_KeepsLastValue()
    {
        var runner = CreateRunner();

        runner.SetCompactionModel("model-a");
        runner.SetCompactionModel("model-b");

        Assert.Equal("model-b", CompactionModelField.GetValue(runner));
    }

    // ── Test G: SetCompactionModel(null) does not affect SendPromptAsync ───────

    [Fact]
    public async Task SetCompactionModel_Null_SendPromptAsyncCompletesWithoutError()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"compaction-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var runner = CreateRunner();
            runner.SetCompactionModel(null);
            runner.SetCustomAgent(WorkerRole.Coder, "you are a coder");

            // This should complete without error — the null path must not attempt
            // to call ChatClientFactory.Create.
            var result = await runner.SendPromptAsync("do something", workDir, TestContext.Current.CancellationToken);

            Assert.NotNull(result);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}

/// <summary>
/// Minimal <see cref="IChatClient"/> stub for compaction tests — returns a single
/// assistant message and signals <see cref="ChatFinishReason.Stop"/> so the
/// <see cref="CodingAgent"/> terminates immediately.
/// </summary>
file sealed class StubChatClientForCompaction : IChatClient
{
    /// <inheritdoc />
    public ChatClientMetadata Metadata => new("stub-compaction", null, "stub-model");

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."))
        {
            FinishReason = ChatFinishReason.Stop,
        };
        return Task.FromResult(response);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return GetStreamingUpdatesAsync("Done.", cancellationToken);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> GetStreamingUpdatesAsync(
        string replyText, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var remaining = replyText.AsMemory();
        const int chunkSize = 10;

        while (!remaining.IsEmpty && !cancellationToken.IsCancellationRequested)
        {
            var chunk = remaining.Length <= chunkSize
                ? remaining
                : remaining[..chunkSize];
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(chunk.ToString())]);
            remaining = remaining.Slice(chunk.Length);
        }

        yield return new ChatResponseUpdate
        {
            FinishReason = ChatFinishReason.Stop,
            Role = ChatRole.Assistant,
        };
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose() { }
}