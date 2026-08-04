using CopilotHive.Worker;

using Microsoft.Extensions.AI;

using System.Reflection;
using System.Runtime.CompilerServices;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Unit tests for reasoning-effort handling in <see cref="SharpCoderRunner"/>:
/// the <c>ResetSessionAsync(model, reasoningEffort, ct)</c> method, the guarantee
/// that reasoning effort is never derived from a model-name suffix, and that
/// <c>CreateChatClient</c> never mutates the already-resolved reasoning effort.
/// </summary>
public sealed class SharpCoderRunnerReasoningTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SharpCoderRunner CreateRunner()
        => new(new StubChatClientForReasoning(), "test-model");

    private static readonly FieldInfo CurrentReasoningField =
        typeof(SharpCoderRunner).GetField("_currentReasoning", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("SharpCoderRunner._currentReasoning field not found.");

    private static ReasoningEffort? GetCurrentReasoning(SharpCoderRunner runner)
        => (ReasoningEffort?)CurrentReasoningField.GetValue(runner);

    // ── Explicit reasoning effort wins ────────────────────────────────────────

    /// <summary>
    /// An explicitly transported reasoning effort is authoritative and must be recorded as-is.
    /// </summary>
    [Fact]
    public async Task ResetSessionAsync_WithReasoningEffort_SetsCurrentReasoning()
    {
        var runner = CreateRunner();

        await runner.ResetSessionAsync("test-model", ReasoningEffort.High, TestContext.Current.CancellationToken);

        Assert.Equal(ReasoningEffort.High, GetCurrentReasoning(runner));
    }

    [Theory]
    [InlineData(ReasoningEffort.None)]
    [InlineData(ReasoningEffort.Low)]
    [InlineData(ReasoningEffort.Medium)]
    [InlineData(ReasoningEffort.High)]
    [InlineData(ReasoningEffort.ExtraHigh)]
    public async Task ResetSessionAsync_WithEachReasoningEffort_SetsCurrentReasoning(ReasoningEffort effort)
    {
        var runner = CreateRunner();

        await runner.ResetSessionAsync("test-model", effort, TestContext.Current.CancellationToken);

        Assert.Equal(effort, GetCurrentReasoning(runner));
    }

    /// <summary>
    /// A colon segment in the model name is part of the name and never affects the effort —
    /// only the explicitly transported value matters.
    /// </summary>
    [Fact]
    public async Task ResetSessionAsync_ExplicitEffortIgnoresModelNameColonSegment()
    {
        var runner = CreateRunner();

        await runner.ResetSessionAsync("test-model:low", ReasoningEffort.ExtraHigh, TestContext.Current.CancellationToken);

        Assert.Equal(ReasoningEffort.ExtraHigh, GetCurrentReasoning(runner));
    }

    // ── No model-suffix fallback ──────────────────────────────────────────────

    /// <summary>
    /// With no explicit effort the value stays unset — the model-name suffix fallback is gone.
    /// </summary>
    [Fact]
    public async Task ResetSessionAsync_WithNullReasoningEffort_DoesNotParseModelSuffix()
    {
        var runner = CreateRunner();

        await runner.ResetSessionAsync("test-model:low", null, TestContext.Current.CancellationToken);

        Assert.Null(GetCurrentReasoning(runner));
    }

    [Theory]
    [InlineData("test-model:none")]
    [InlineData("test-model:low")]
    [InlineData("test-model:medium")]
    [InlineData("test-model:high")]
    [InlineData("test-model:extra_high")]
    public async Task ResetSessionAsync_WithNullReasoningEffort_NoSuffixIsEverParsed(string model)
    {
        var runner = CreateRunner();

        await runner.ResetSessionAsync(model, null, TestContext.Current.CancellationToken);

        Assert.Null(GetCurrentReasoning(runner));
    }

    [Fact]
    public async Task ResetSessionAsync_WithNullReasoningEffortAndNoSuffix_SetsNull()
    {
        var runner = CreateRunner();

        await runner.ResetSessionAsync("test-model", null, TestContext.Current.CancellationToken);

        Assert.Null(GetCurrentReasoning(runner));
    }

    /// <summary>
    /// Resetting without an explicit effort must clear a previously-set effort rather than
    /// leaving stale state behind from an earlier task.
    /// </summary>
    [Fact]
    public async Task ResetSessionAsync_WithNullReasoningEffort_ClearsPreviousEffort()
    {
        var runner = CreateRunner();
        await runner.ResetSessionAsync("test-model", ReasoningEffort.High, TestContext.Current.CancellationToken);
        Assert.Equal(ReasoningEffort.High, GetCurrentReasoning(runner));

        await runner.ResetSessionAsync("test-model", null, TestContext.Current.CancellationToken);

        Assert.Null(GetCurrentReasoning(runner));
    }

    // ── Session clearing still happens on ResetSessionAsync ──────────────────

    [Fact]
    public async Task ResetSessionAsync_WithReasoningEffort_ClearsSession()
    {
        var runner = CreateRunner();
        runner.SetSession(SharpCoder.AgentSession.Create("session-to-clear"));
        Assert.NotNull(runner.GetSession());

        await runner.ResetSessionAsync("test-model", ReasoningEffort.High, TestContext.Current.CancellationToken);

        Assert.Null(runner.GetSession());
    }

    // ── CreateChatClient must not mutate _currentReasoning ────────────────────

    /// <summary>
    /// Regression guard: <c>CreateChatClient</c> used to assign <c>_currentReasoning</c> from the
    /// model-name suffix, silently clobbering the authoritative value that
    /// <c>ResetSessionAsync</c> had just set. It must now only create the client.
    /// <para>
    /// The old assignment happened before the client was actually constructed, so this assertion
    /// holds (and fails if the assignment is reintroduced) even when client construction throws
    /// for lack of provider credentials in the test environment.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CreateChatClient_DoesNotOverwriteCurrentReasoning()
    {
        var runner = CreateRunner();
        await runner.ResetSessionAsync("test-model", ReasoningEffort.High, TestContext.Current.CancellationToken);
        Assert.Equal(ReasoningEffort.High, GetCurrentReasoning(runner));

        var createChatClient = typeof(SharpCoderRunner).GetMethod(
            "CreateChatClient", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(createChatClient);

        try
        {
            // A conflicting suffix: the buggy version would overwrite High with Low here.
            createChatClient!.Invoke(runner, ["test-model:low"]);
        }
        catch (TargetInvocationException)
        {
            // Constructing a real provider client may fail without credentials — irrelevant,
            // because the (removed) mutation happened before construction.
        }

        Assert.Equal(ReasoningEffort.High, GetCurrentReasoning(runner));
    }

    /// <summary>
    /// End-to-end via the public surface: <c>ConnectAsync</c> must not disturb an effort that was
    /// already resolved by <c>ResetSessionAsync</c>.
    /// </summary>
    [Fact]
    public async Task ConnectAsync_AfterResetSessionAsync_PreservesCurrentReasoning()
    {
        var runner = CreateRunner();
        await runner.ResetSessionAsync("test-model", ReasoningEffort.High, TestContext.Current.CancellationToken);

        await runner.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ReasoningEffort.High, GetCurrentReasoning(runner));
    }
}

/// <summary>
/// Minimal <see cref="IChatClient"/> stub for reasoning tests — returns a single assistant message
/// and signals <see cref="ChatFinishReason.Stop"/> so <see cref="SharpCoder.CodingAgent"/> terminates.
/// </summary>
file sealed class StubChatClientForReasoning : IChatClient
{
    /// <inheritdoc />
    public ChatClientMetadata Metadata => new("stub-reasoning", null, "stub-model");

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
        => GetStreamingUpdatesAsync(cancellationToken);

    private static async IAsyncEnumerable<ChatResponseUpdate> GetStreamingUpdatesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Done.")]);
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
