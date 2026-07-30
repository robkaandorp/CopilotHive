using System.Reflection;

using CopilotHive.Actors;
using CopilotHive.Orchestration;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using SharpCoder;
using SharpCoder.SubAgents;

using Xunit;

namespace CopilotHive.Tests.Actors;

/// <summary>
/// Tests for the Brain sub-agent wiring: <c>BuildSubAgentOptions</c> and the immutable
/// model snapshot shared with <see cref="DistributedBrain"/>.
/// </summary>
public class BrainActorSubAgentTests
{
    private const BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "stub")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempPath(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
    }

    private static BrainActor CreateActor(
        string stateDir,
        IReadOnlyList<SubAgentModelEntry>? subAgentModels,
        bool subAgentsEnabled,
        Func<string, IChatClient>? chatClientFactory = null) =>
        new("copilot/test-model", 100_000, stateDir, NullLogger.Instance,
            chatClientFactory: chatClientFactory ?? (_ => new StubChatClient()),
            subAgentModels: subAgentModels,
            subAgentsEnabled: subAgentsEnabled);

    /// <summary>Raw, parent-owned resources returned by the private <c>PrepareChildResources</c>.</summary>
    private readonly record struct RawChildResources(
        IChatClient ChatClient,
        bool OwnsClient,
        IChatClient? CompactionClient,
        AgentOptions Options);

    private static RawChildResources PrepareChildResources(BrainActor actor)
    {
        var boxed = typeof(BrainActor)
            .GetMethod("PrepareChildResources", NonPublicInstance)!
            .Invoke(actor, ["goal-1"])!;
        var type = boxed.GetType();

        return new RawChildResources(
            (IChatClient)type.GetProperty("ChatClient")!.GetValue(boxed)!,
            (bool)type.GetProperty("OwnsClient")!.GetValue(boxed)!,
            (IChatClient?)type.GetProperty("CompactionClient")!.GetValue(boxed),
            (AgentOptions)type.GetProperty("Options")!.GetValue(boxed)!);
    }

    /// <summary>
    /// Disposes every raw resource the reflected call produced. The parent normally hands these
    /// to a child actor; here the test owns them, so it must release them itself.
    /// </summary>
    private static void DisposeRawResources(RawChildResources resources)
    {
        if (resources.CompactionClient is not null
            && !ReferenceEquals(resources.CompactionClient, resources.ChatClient))
        {
            resources.CompactionClient.Dispose();
        }

        if (resources.OwnsClient)
        {
            resources.ChatClient.Dispose();
        }
    }

    private static SubAgentOptions? BuildSubAgentOptions(BrainActor actor) =>
        (SubAgentOptions?)typeof(BrainActor)
            .GetMethod("BuildSubAgentOptions", NonPublicInstance)!
            .Invoke(actor, null);

    [Fact]
    public async Task BuildSubAgentOptions_UsesConfiguredDescription_WhenPresent()
    {
        var dir = CreateTempDir();
        try
        {
            IReadOnlyList<SubAgentModelEntry> snapshot =
            [
                new("copilot/model-a", 128_000, "Great for wide code search"),
                new("copilot/model-b", 64_000, "   "),
            ];

            await using var actor = CreateActor(dir, snapshot, subAgentsEnabled: true);
            var options = BuildSubAgentOptions(actor);

            Assert.NotNull(options);
            Assert.Equal("Great for wide code search", options!.AvailableModels[0].Description);
            Assert.Equal("Configured model, 64K context window", options.AvailableModels[1].Description);
        }
        finally
        {
            DeleteTempPath(dir);        }
    }

    [Fact]
    public async Task BuildSubAgentOptions_WithSnapshot_BuildsModelCatalogAndLimits()
    {
        var dir = CreateTempDir();
        try
        {
            IReadOnlyList<SubAgentModelEntry> snapshot =
            [
                new("copilot/model-a", 128_000, null),
                new("copilot/model-b", null, null),
            ];

            await using var actor = CreateActor(dir, snapshot, subAgentsEnabled: true);
            var options = BuildSubAgentOptions(actor);

            Assert.NotNull(options);
            Assert.Equal(2, options!.AvailableModels.Count);

            Assert.Equal("copilot/model-a", options.AvailableModels[0].Id);
            Assert.Contains("Configured model", options.AvailableModels[0].Description);
            Assert.Contains("128K", options.AvailableModels[0].Description);
            Assert.Equal(128_000, options.AvailableModels[0].ContextWindow);

            Assert.Equal("copilot/model-b", options.AvailableModels[1].Id);
            Assert.Equal("Configured model", options.AvailableModels[1].Description);
            Assert.Null(options.AvailableModels[1].ContextWindow);

            Assert.Equal(2, options.MaxConcurrentSubAgents);
            Assert.Equal(TimeSpan.FromMinutes(5), options.DefaultTimeout);
            Assert.Equal(TimeSpan.FromMinutes(15), options.MaxTimeout);
            Assert.Equal(8_000, options.MaxSummaryChars);
            Assert.NotNull(options.ClientFactory);
            Assert.Null(options.DefaultClient);
            Assert.False(options.DefaultEnableBash);
            Assert.True(options.DefaultEnableFileOps);
            Assert.False(options.DefaultEnableFileWrites);
            Assert.False(options.DefaultEnableSkills);
        }
        finally { DeleteTempPath(dir); }
    }

    [Fact]
    public async Task BuildSubAgentOptions_CatalogIdsHaveNoReasoningSuffixes()
    {
        var dir = CreateTempDir();
        try
        {
            IReadOnlyList<SubAgentModelEntry> snapshot = [new("copilot/model-a", 64_000, null)];
            await using var actor = CreateActor(dir, snapshot, subAgentsEnabled: true);

            var options = BuildSubAgentOptions(actor);

            Assert.NotNull(options);
            Assert.All(options!.AvailableModels, m => Assert.DoesNotContain(":", m.Id));
        }
        finally { DeleteTempPath(dir); }
    }

    [Fact]
    public async Task BuildSubAgentOptions_WhenDisabled_ReturnsNull()
    {
        var dir = CreateTempDir();
        try
        {
            IReadOnlyList<SubAgentModelEntry> snapshot = [new("copilot/model-a", 64_000, null)];
            await using var actor = CreateActor(dir, snapshot, subAgentsEnabled: false);

            Assert.Null(BuildSubAgentOptions(actor));
        }
        finally { DeleteTempPath(dir); }
    }

    [Fact]
    public async Task BuildSubAgentOptions_WhenSnapshotEmptyOrNull_ReturnsNull()
    {
        var dir = CreateTempDir();
        try
        {
            await using var empty = CreateActor(dir, [], subAgentsEnabled: true);
            Assert.Null(BuildSubAgentOptions(empty));

            await using var missing = CreateActor(dir, null, subAgentsEnabled: true);
            Assert.Null(BuildSubAgentOptions(missing));
        }
        finally { DeleteTempPath(dir); }
    }

    [Fact]
    public async Task BuildSubAgentOptions_ClientFactory_DelegatesToInjectedChatClientFactory()
    {
        var dir = CreateTempDir();
        try
        {
            var requested = new List<string>();
            var stub = new StubChatClient();
            IReadOnlyList<SubAgentModelEntry> snapshot = [new("copilot/model-a", 64_000, null)];

            await using var actor = CreateActor(dir, snapshot, subAgentsEnabled: true,
                chatClientFactory: model => { requested.Add(model); return stub; });

            var options = BuildSubAgentOptions(actor);
            Assert.NotNull(options);

            var client = options!.ClientFactory!("copilot/model-a");
            try
            {
                Assert.Same(stub, client);
                Assert.Equal(["copilot/model-a"], requested);
            }
            finally
            {
                client.Dispose();
            }
        }
        finally { DeleteTempPath(dir); }
    }

    [Fact]
    public async Task ChildOptions_CarrySubAgentOptions_WhenEnabled()
    {
        var dir = CreateTempDir();
        try
        {
            IReadOnlyList<SubAgentModelEntry> snapshot = [new("copilot/model-a", 64_000, null)];
            await using var actor = CreateActor(dir, snapshot, subAgentsEnabled: true);

            var resources = PrepareChildResources(actor);
            try
            {
                Assert.NotNull(resources.Options.SubAgents);
                Assert.Equal(2, resources.Options.SubAgents!.MaxConcurrentSubAgents);
            }
            finally
            {
                DisposeRawResources(resources);
            }
        }
        finally { DeleteTempPath(dir); }
    }

    [Fact]
    public async Task ChildOptions_HaveNoSubAgentOptions_WhenDisabled()
    {
        var dir = CreateTempDir();
        try
        {
            await using var actor = CreateActor(dir, null, subAgentsEnabled: false);

            var resources = PrepareChildResources(actor);
            try
            {
                Assert.Null(resources.Options.SubAgents);
            }
            finally
            {
                DisposeRawResources(resources);
            }
        }
        finally { DeleteTempPath(dir); }
    }
}
