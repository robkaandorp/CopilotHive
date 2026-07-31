using CopilotHive.Services;
using CopilotHive.Worker;
using CopilotHive.Workers;

using Microsoft.Extensions.AI;

using SharpCoder;
using SharpCoder.SubAgents;

using System.Runtime.CompilerServices;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Unit tests for <see cref="SharpCoderRunner"/> sub-agent wiring:
/// verifies <see cref="SharpCoderRunner.BuildSubAgentOptions"/>, ClientFactory delegation,
/// per-prompt disposal, capability ceilings, and no double-disposal of factory-created clients.
/// </summary>
public sealed class SharpCoderRunnerSubAgentTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SharpCoderRunner CreateRunnerWithStub()
        => new(new SubAgentStubChatClient(), "test-model");

    private static SharpCoderRunner CreateRunnerProduction()
        => new();

    private static string CreateWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"subagent-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── 1. BuildSubAgentOptions seam tests ─────────────────────────────────────

    [Fact]
    public async Task BuildSubAgentOptions_NonEmptyCatalog_ReturnsPopulatedSubAgents()
    {
        var runner = CreateRunnerWithStub();
        try
        {
            runner.SetSubAgentModels(
            [
                new SubAgentModelDto { Id = "model-a", ContextWindow = 200_000, Description = "Big model" },
                new SubAgentModelDto { Id = "model-b", ContextWindow = null, Description = "Unknown" },
            ]);

            var result = runner.BuildSubAgentOptions();

            Assert.NotNull(result);
            Assert.Equal(2, result.MaxConcurrentSubAgents);
            Assert.Equal(TimeSpan.FromMinutes(5), result.DefaultTimeout);
            Assert.Equal(TimeSpan.FromMinutes(15), result.MaxTimeout);
            Assert.Equal(8_000, result.MaxSummaryChars);
            Assert.Equal(2, result.AvailableModels.Count);

            Assert.Equal("model-a", result.AvailableModels[0].Id);
            Assert.Equal("Big model", result.AvailableModels[0].Description);
            Assert.Equal(200_000, result.AvailableModels[0].ContextWindow);

            Assert.Equal("model-b", result.AvailableModels[1].Id);
            Assert.Equal("Unknown", result.AvailableModels[1].Description);
            Assert.Null(result.AvailableModels[1].ContextWindow);
        }
        finally
        {
            await runner.DisposeAsync();
        }
    }

    [Fact]
    public async Task BuildSubAgentOptions_EmptyCatalog_ReturnsNull()
    {
        var runner = CreateRunnerWithStub();
        try
        {
            // No SetSubAgentModels called — default is empty list
            var result = runner.BuildSubAgentOptions();
            Assert.Null(result);
        }
        finally
        {
            await runner.DisposeAsync();
        }
    }

    [Fact]
    public async Task BuildSubAgentOptions_ExplicitlyEmptyList_ReturnsNull()
    {
        var runner = CreateRunnerWithStub();
        try
        {
            runner.SetSubAgentModels([]);

            var result = runner.BuildSubAgentOptions();
            Assert.Null(result);
        }
        finally
        {
            await runner.DisposeAsync();
        }
    }

    [Fact]
    public async Task BuildSubAgentOptions_FiltersBlankAndWhitespaceNames()
    {
        var runner = CreateRunnerWithStub();
        try
        {
            runner.SetSubAgentModels(
            [
                new SubAgentModelDto { Id = "valid-model", ContextWindow = 100_000 },
                new SubAgentModelDto { Id = "   ", ContextWindow = 200_000 },
                new SubAgentModelDto { Id = "", ContextWindow = 300_000 },
            ]);

            var result = runner.BuildSubAgentOptions();

            Assert.NotNull(result);
            Assert.Single(result.AvailableModels);
            Assert.Equal("valid-model", result.AvailableModels[0].Id);
        }
        finally
        {
            await runner.DisposeAsync();
        }
    }

    // ── SupportsVision mapping (non-nullable source → direct) ────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BuildSubAgentOptions_MapsSupportsVisionDirectly(bool vision)
    {
        var runner = CreateRunnerWithStub();
        try
        {
            runner.SetSubAgentModels(
            [
                new SubAgentModelDto { Id = "model-a", ContextWindow = 200_000, SupportsVision = vision },
            ]);

            var result = runner.BuildSubAgentOptions();

            Assert.NotNull(result);
            Assert.Equal(vision, result!.AvailableModels[0].SupportsVision);
        }
        finally
        {
            await runner.DisposeAsync();
        }
    }

    [Fact]
    public async Task BuildSubAgentOptions_SupportsVisionDefaultsToFalseWhenUnset()
    {
        var runner = CreateRunnerWithStub();
        try
        {
            // SubAgentModelDto.SupportsVision defaults to false (non-nullable, default false)
            runner.SetSubAgentModels(
            [
                new SubAgentModelDto { Id = "model-a", ContextWindow = 200_000 },
            ]);

            var result = runner.BuildSubAgentOptions();

            Assert.NotNull(result);
            Assert.False(result!.AvailableModels[0].SupportsVision);
        }
        finally
        {
            await runner.DisposeAsync();
        }
    }

    // ── 2. ClientFactory delegation tests ──────────────────────────────────────

    [Fact]
    public async Task BuildSubAgentOptions_ClientFactory_WithTestClientFactory_DelegatesToIt()
    {
        var fakeClient = new SubAgentStubChatClient();
        var runner = new SharpCoderRunner(fakeClient, "test-model");
        try
        {
            runner.SetSubAgentModels(
            [
                new SubAgentModelDto { Id = "model-a", ContextWindow = 200_000 },
            ]);

            var result = runner.BuildSubAgentOptions();

            Assert.NotNull(result);
            Assert.NotNull(result.ClientFactory);

            // The factory returns the injected client itself; the runner owns it and
            // disposes it in DisposeAsync, so the test must not dispose it separately.
            var returnedClient = result.ClientFactory!("model-a");
            Assert.Same(fakeClient, returnedClient);
        }
        finally
        {
            await runner.DisposeAsync();
        }
    }

    [Fact]
    public async Task BuildSubAgentOptions_ClientFactory_ProductionNull_DelegatesToClientCreationSeam()
    {
        var runner = CreateRunnerProduction();
        var seamClient = new SubAgentStubChatClient();
        try
        {
            runner.ClientCreationSeam = _ => seamClient;
            runner.SetSubAgentModels(
            [
                new SubAgentModelDto { Id = "model-a", ContextWindow = 200_000 },
            ]);

            var result = runner.BuildSubAgentOptions();

            Assert.NotNull(result);
            Assert.NotNull(result.ClientFactory);

            var returnedClient = result.ClientFactory!("model-a");
            Assert.Same(seamClient, returnedClient);
        }
        finally
        {
            // The runner never connected, so it owns no chat client — the test owns seamClient.
            await runner.DisposeAsync();
            seamClient.Dispose();
        }
    }

    [Fact]
    public async Task BuildSubAgentOptions_ClientFactory_TestClientFactoryTakesPrecedenceOverSeam()
    {
        var factoryClient = new SubAgentStubChatClient();
        var seamClient = new SubAgentStubChatClient();
        var runner = new SharpCoderRunner(factoryClient, "test-model");
        try
        {
            runner.ClientCreationSeam = _ => seamClient;
            runner.SetSubAgentModels(
            [
                new SubAgentModelDto { Id = "model-a", ContextWindow = 200_000 },
            ]);

            var result = runner.BuildSubAgentOptions();

            Assert.NotNull(result);
            Assert.NotNull(result.ClientFactory);

            // _clientFactory takes precedence over ClientCreationSeam
            var returnedClient = result.ClientFactory!("model-a");
            Assert.Same(factoryClient, returnedClient);
        }
        finally
        {
            // The runner owns factoryClient; seamClient was never handed to the runner.
            await runner.DisposeAsync();
            seamClient.Dispose();
        }
    }

    // ── 3. Per-prompt disposal tests ──────────────────────────────────────────

    [Fact]
    public async Task SendPromptAsync_SuccessPath_DisposesCodingAgent()
    {
        var workDir = CreateWorkDir();
        var runner = CreateRunnerWithStub();
        try
        {
            runner.SetCustomAgent(WorkerRole.Coder, "you are a coder");

            CodingAgent? capturedAgent = null;
            runner.OnAgentCreated = agent => capturedAgent = agent;

            await runner.SendPromptAsync("do something", workDir, TestContext.Current.CancellationToken);

            Assert.NotNull(capturedAgent);

            // After SendPromptAsync completes, the agent should be disposed.
            // A subsequent ExecuteAsync on a disposed agent throws ObjectDisposedException.
            Assert.Throws<ObjectDisposedException>(() =>
                capturedAgent!.ExecuteAsync("test", TestContext.Current.CancellationToken).GetAwaiter().GetResult());
        }
        finally
        {
            await runner.DisposeAsync();
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task SendPromptAsync_ExceptionPath_DisposesCodingAgent()
    {
        var workDir = CreateWorkDir();
        var runner = CreateRunnerWithStub();
        try
        {
            runner.SetCustomAgent(WorkerRole.Coder, "you are a coder");

            CodingAgent? capturedAgent = null;
            // Throw from OnAgentCreated to trigger the exception path in SendPromptAsync.
            // The `await using` must dispose the agent even when this callback throws.
            runner.OnAgentCreated = agent =>
            {
                capturedAgent = agent;
                throw new InvalidOperationException("Simulated failure after agent creation");
            };

            // SendPromptAsync should throw because OnAgentCreated throws.
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await runner.SendPromptAsync("do something", workDir, TestContext.Current.CancellationToken));

            Assert.NotNull(capturedAgent);

            // Even on the exception path, the agent must have been disposed.
            Assert.Throws<ObjectDisposedException>(() =>
                capturedAgent!.ExecuteAsync("test", TestContext.Current.CancellationToken).GetAwaiter().GetResult());
        }
        finally
        {
            await runner.DisposeAsync();
            Directory.Delete(workDir, recursive: true);
        }
    }

    // ── 4. Capability ceiling tests (via OnAgentOptionsCreated) ────────────────

    [Fact]
    public async Task SendPromptAsync_ReviewerRole_EnableFileWritesFalseAndSubAgentsNotNull()
    {
        var workDir = CreateWorkDir();
        var runner = CreateRunnerWithStub();
        try
        {
            runner.SetCustomAgent(WorkerRole.Reviewer, "you are a reviewer");
            runner.SetSubAgentModels(
            [
                new SubAgentModelDto { Id = "model-a", ContextWindow = 200_000 },
            ]);

            AgentOptions? capturedOptions = null;
            runner.OnAgentOptionsCreated = opts => capturedOptions = opts;

            await runner.SendPromptAsync("review it", workDir, TestContext.Current.CancellationToken);

            Assert.NotNull(capturedOptions);
            Assert.False(capturedOptions!.EnableFileWrites);
            Assert.NotNull(capturedOptions.SubAgents);

            // SharpCoder 0.13.1's SubAgentManager clamps sub-agent capabilities against parent flags
            // — see SubAgentManager tests in SharpCoder. The assertion above proves the parent
            // capability ceiling; the effective sub-agent behavior relies on SharpCoder's clamp.
        }
        finally
        {
            await runner.DisposeAsync();
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task SendPromptAsync_ImproverRole_EnableBashFalseAndSubAgentsNotNull()
    {
        var workDir = CreateWorkDir();
        var runner = CreateRunnerWithStub();
        try
        {
            runner.SetCustomAgent(WorkerRole.Improver, "you are an improver");
            runner.SetSubAgentModels(
            [
                new SubAgentModelDto { Id = "model-a", ContextWindow = 200_000 },
            ]);

            AgentOptions? capturedOptions = null;
            runner.OnAgentOptionsCreated = opts => capturedOptions = opts;

            await runner.SendPromptAsync("improve it", workDir, TestContext.Current.CancellationToken);

            Assert.NotNull(capturedOptions);
            Assert.False(capturedOptions!.EnableBash);
            Assert.NotNull(capturedOptions.SubAgents);

            // SharpCoder 0.13.1's SubAgentManager clamps sub-agent capabilities against parent flags
            // — see SubAgentManager tests in SharpCoder. The assertion above proves the parent
            // capability ceiling; the effective sub-agent behavior relies on SharpCoder's clamp.
        }
        finally
        {
            await runner.DisposeAsync();
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task SendPromptAsync_ReviewerRoleWithEmptyCatalog_SubAgentsIsNull()
    {
        var workDir = CreateWorkDir();
        var runner = CreateRunnerWithStub();
        try
        {
            runner.SetCustomAgent(WorkerRole.Reviewer, "you are a reviewer");
            // Empty catalog — sub-agents disabled

            AgentOptions? capturedOptions = null;
            runner.OnAgentOptionsCreated = opts => capturedOptions = opts;

            await runner.SendPromptAsync("review it", workDir, TestContext.Current.CancellationToken);

            Assert.NotNull(capturedOptions);
            Assert.False(capturedOptions!.EnableFileWrites);
            Assert.Null(capturedOptions.SubAgents);
        }
        finally
        {
            await runner.DisposeAsync();
            Directory.Delete(workDir, recursive: true);
        }
    }

    // ── 5. No double-disposal of factory-created sub-agent clients ─────────────

    /// <summary>
    /// Factory-created sub-agent clients are owned by SharpCoder's SubAgentManager, never by the
    /// runner. This test actually INVOKES <see cref="SubAgentOptions.ClientFactory"/> to create a
    /// tracked client, then proves neither <c>SendPromptAsync</c> nor the runner's
    /// <c>DisposeAsync</c> disposes it. The test owns the created client and disposes it itself.
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_DoesNotDisposeFactoryCreatedSubAgentClient()
    {
        var workDir = CreateWorkDir();
        var runner = CreateRunnerProduction();

        // The seam serves the main chat client for a null model id (used by ConnectAsync) and
        // the tracked counting client for any sub-agent model id.
        var mainClient = new SubAgentStubChatClient();
        var subAgentClient = new CountingStubChatClient();

        try
        {
            runner.ClientCreationSeam = modelId => modelId is null ? mainClient : subAgentClient;
            runner.SetSubAgentModels(
            [
                new SubAgentModelDto { Id = "model-a", ContextWindow = 200_000 },
            ]);
            runner.SetCustomAgent(WorkerRole.Coder, "you are a coder");

            await runner.ConnectAsync(TestContext.Current.CancellationToken);

            SubAgentOptions? capturedSubAgents = null;
            runner.OnAgentOptionsCreated = opts => capturedSubAgents = opts.SubAgents;

            await runner.SendPromptAsync("do something", workDir, TestContext.Current.CancellationToken);

            Assert.NotNull(capturedSubAgents);
            Assert.NotNull(capturedSubAgents!.ClientFactory);

            // Actually invoke the factory so a real tracked client exists.
            var created = capturedSubAgents.ClientFactory!("model-a");
            Assert.Same(subAgentClient, created);

            // The runner must NOT have disposed the factory-created client.
            Assert.Equal(0, subAgentClient.DisposeCallCount);

            // Dispose the runner once, so we can also assert disposal does not cascade.
            // Set runner to null afterwards so the finally block does not dispose it again.
            await runner.DisposeAsync();
            runner = null!;

            // Nor may the runner's DisposeAsync dispose it — it only owns the main chat client.
            Assert.Equal(0, subAgentClient.DisposeCallCount);

            // The test owns the factory-created client here (no SubAgentManager took ownership),
            // so it disposes it — proving disposal was still pending, i.e. not double-disposed.
            created.Dispose();
            Assert.Equal(1, subAgentClient.DisposeCallCount);
        }
        finally
        {
            if (runner is not null)
                await runner.DisposeAsync();
            // Only dispose if the success path hasn't already done so (created.Dispose() at line 435).
            // This prevents double-disposal: success path disposes once, exception path disposes here.
            if (subAgentClient.DisposeCallCount == 0)
                subAgentClient.Dispose();
            Directory.Delete(workDir, recursive: true);
        }
    }
}

// ── Stub IChatClient implementations ─────────────────────────────────────────

/// <summary>
/// Minimal <see cref="IChatClient"/> stub that returns a single assistant message
/// with <see cref="ChatFinishReason.Stop"/> so <see cref="CodingAgent"/> terminates immediately.
/// </summary>
file sealed class SubAgentStubChatClient : IChatClient
{
    public ChatClientMetadata Metadata => new("subagent-stub", null, "stub-model");

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

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => GetStreamingUpdatesAsync("Done.", cancellationToken);

    private static async IAsyncEnumerable<ChatResponseUpdate> GetStreamingUpdatesAsync(
        string replyText, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(replyText)]);
        yield return new ChatResponseUpdate
        {
            FinishReason = ChatFinishReason.Stop,
            Role = ChatRole.Assistant,
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>
/// Stub <see cref="IChatClient"/> that counts <see cref="Dispose"/> calls,
/// used to verify the runner does NOT dispose factory-created sub-agent clients.
/// </summary>
file sealed class CountingStubChatClient : IChatClient
{
    public int DisposeCallCount { get; private set; }

    public ChatClientMetadata Metadata => new("counting-stub", null, "counting-model");

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

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => GetStreamingUpdatesAsync("Done.", cancellationToken);

    private static async IAsyncEnumerable<ChatResponseUpdate> GetStreamingUpdatesAsync(
        string replyText, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(replyText)]);
        yield return new ChatResponseUpdate
        {
            FinishReason = ChatFinishReason.Stop,
            Role = ChatRole.Assistant,
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() => DisposeCallCount++;
}
