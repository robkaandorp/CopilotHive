using System.Reflection;
using System.Text.Json;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Orchestration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SharpCoder;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Integration tests for <see cref="ComposerAgentService"/> covering agent lifecycle,
/// connection management, session-load recovery, cancellation cleanup, model switching,
/// disposal idempotency, and live config projection.
/// </summary>
public sealed class ComposerAgentServiceTests
{
    // ── Helpers ──

    private static ComposerAgentService CreateService(
        string stateDir,
        Func<string, IChatClient>? chatClientFactory = null,
        HiveConfigFile? hiveConfig = null,
        string model = "test-model",
        int maxContextTokens = 64000,
        int maxSteps = 50,
        ReasoningEffort? reasoningEffort = null,
        string? compactionModel = null,
        LlmSessionRegistry? sessionRegistry = null,
        IReadOnlyList<string>? startupAvailableModels = null,
        IBrainRepoManager? repoManager = null,
        Action? onCompacting = null,
        Action<CompactionResult>? onCompacted = null,
        ILogger? logger = null)
    {
        return new ComposerAgentService(
            model,
            maxContextTokens,
            maxSteps,
            reasoningEffort,
            hiveConfig,
            "system prompt",
            new List<AITool>(),
            repoManager,
            stateDir,
            compactionModel,
            logger ?? NullLogger<ComposerAgentService>.Instance,
            chatClientFactory,
            sessionRegistry,
            startupAvailableModels ?? [model],
            onCompacting,
            onCompacted);
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"composer-agent-svc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Reads a private field value from <paramref name="obj"/> via reflection.
    /// </summary>
    private static T? GetField<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {obj.GetType().Name}");
        return (T?)field.GetValue(obj);
    }

    // ── 1. AgentOptions getter throws before RecreateAgent ──

    [Fact]
    public void AgentOptions_Getter_ThrowsBeforeRecreateAgent()
    {
        var stateDir = CreateTempDir();
        try
        {
            var service = CreateService(stateDir);

            // Service has not been connected, so _agentOptions is null.
            var ex = Assert.Throws<InvalidOperationException>(() => service.AgentOptions);
            Assert.Contains("AgentOptions", ex.Message);
            Assert.Contains("ConnectAsync", ex.Message);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── 2. RecreateAgent throws when not connected ──

    [Fact]
    public void RecreateAgent_WhenNotConnected_ThrowsInvalidOperationException()
    {
        var stateDir = CreateTempDir();
        try
        {
            var service = CreateService(stateDir);

            var ex = Assert.Throws<InvalidOperationException>(() => service.RecreateAgent());
            Assert.Contains("Composer not connected", ex.Message);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── 3. ResetSession throws when not connected ──

    [Fact]
    public void ResetSession_WhenNotConnected_ThrowsInvalidOperationException()
    {
        var stateDir = CreateTempDir();
        try
        {
            var service = CreateService(stateDir);

            var ex = Assert.Throws<InvalidOperationException>(() => service.ResetSession());
            Assert.Contains("Composer not connected", ex.Message);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── 4. ConnectAsync session-load corruption recovery ──

    [Fact]
    public async Task ConnectAsync_CorruptedSessionFile_RecoverWithFreshSession()
    {
        var stateDir = CreateTempDir();
        try
        {
            // Write a corrupted JSON session file.
            var sessionFile = Path.Combine(stateDir, "composer-session.json");
            await File.WriteAllTextAsync(sessionFile, "{invalid json", TestContext.Current.CancellationToken);

            var mockClient = new Mock<IChatClient>();
            var service = CreateService(stateDir, chatClientFactory: _ => mockClient.Object);

            await service.ConnectAsync(TestContext.Current.CancellationToken);

            // Service should be connected despite the corruption.
            Assert.True(service.IsConnected);

            // Session should be fresh — either 0 messages or just a system prompt.
            var session = service.Session;
            Assert.True(session.MessageHistory.Count <= 1,
                $"Fresh session should have 0 or 1 messages, had {session.MessageHistory.Count}");
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── 5. ConnectAsync cancellation cleanup ──

    [Fact]
    public async Task ConnectAsync_PreCancelledToken_ThrowsAndCleansUpState()
    {
        var stateDir = CreateTempDir();
        try
        {
            // Write a valid session file so the cancellation hits during session load.
            var sessionFile = Path.Combine(stateDir, "composer-session.json");
            var validSession = AgentSession.Create("composer");
            validSession.MessageHistory.Add(new ChatMessage(ChatRole.User, "hello"));
            await validSession.SaveAsync(sessionFile, CancellationToken.None);

            var mockClient = new Mock<IChatClient>();
            var service = CreateService(stateDir, chatClientFactory: _ => mockClient.Object);

            // Pre-cancel the token.
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // TaskCanceledException derives from OperationCanceledException, so use ThrowsAnyAsync.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.ConnectAsync(cts.Token));

            // All state should be cleared.
            Assert.False(service.IsConnected);
            Assert.Null(GetField<IChatClient>(service, "_chatClient"));
            Assert.Null(GetField<CodingAgent>(service, "_agent"));
            Assert.Null(GetField<AgentOptions>(service, "_agentOptions"));
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── 6. SwitchModelAsync session preservation ──

    [Fact]
    public async Task SwitchModelAsync_PreservesSessionAndKeepsSameReference()
    {
        var stateDir = CreateTempDir();
        try
        {
            var mockClient = new Mock<IChatClient>();
            var hiveConfig = new HiveConfigFile
            {
                Models = new ModelsConfig
                {
                    AvailableModels =
                    [
                        new ModelEntry { Name = "model-a" },
                        new ModelEntry { Name = "model-b" }
                    ]
                }
            };

            var service = CreateService(
                stateDir,
                chatClientFactory: _ => mockClient.Object,
                hiveConfig: hiveConfig,
                model: "model-a");

            await service.ConnectAsync(TestContext.Current.CancellationToken);

            // Add messages to the session via the public Session property.
            var session = service.Session;
            session.MessageHistory.Add(new ChatMessage(ChatRole.User, "message-1"));
            session.MessageHistory.Add(new ChatMessage(ChatRole.Assistant, "response-1"));
            var messageCount = session.MessageHistory.Count;
            var sessionRef = session;

            // Switch to model-b.
            await service.SwitchModelAsync("model-b");

            // Session reference should be the same object (preserved, not reloaded).
            Assert.Same(sessionRef, service.Session);

            // Messages should still be there.
            Assert.Equal(messageCount, service.Session.MessageHistory.Count);
            Assert.Contains(service.Session.MessageHistory, m => m.Text == "message-1");
            Assert.Contains(service.Session.MessageHistory, m => m.Text == "response-1");
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── 7. DisposeAsync idempotency and same-instance-once disposal ──

    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        var stateDir = CreateTempDir();
        try
        {
            var mockClient = new Mock<IChatClient>();
            var service = CreateService(stateDir, chatClientFactory: _ => mockClient.Object);

            await service.ConnectAsync(TestContext.Current.CancellationToken);

            // First disposal should not throw.
            await service.DisposeAsync();

            // Second disposal should not throw (idempotent).
            await service.DisposeAsync();

            // Service should be disconnected.
            Assert.False(service.IsConnected);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task DisposeAsync_WithSeparateCompactionClient_DisposesEachOnce()
    {
        var stateDir = CreateTempDir();
        try
        {
            // When a compaction model is configured, _compactionChatClient is separate from _chatClient.
            var mainClient = new Mock<IChatClient>();
            var compactionClient = new Mock<IChatClient>();

            var service = CreateService(
                stateDir,
                chatClientFactory: model => model == "compaction-model" ? compactionClient.Object : mainClient.Object,
                compactionModel: "compaction-model");

            await service.ConnectAsync(TestContext.Current.CancellationToken);

            // Verify both clients are set and are different instances.
            var chatClient = GetField<IChatClient>(service, "_chatClient");
            var compactionChatClient = GetField<IChatClient>(service, "_compactionChatClient");
            Assert.NotNull(chatClient);
            Assert.NotNull(compactionChatClient);
            Assert.NotSame(chatClient, compactionChatClient);

            await service.DisposeAsync();

            // Each client should be disposed exactly once.
            mainClient.Verify(c => c.Dispose(), Times.Once);
            compactionClient.Verify(c => c.Dispose(), Times.Once);

            // State should be cleared.
            Assert.Null(GetField<IChatClient>(service, "_chatClient"));
            Assert.Null(GetField<IChatClient>(service, "_compactionChatClient"));
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task DisposeAsync_WithoutCompactionModel_DisposesChatClientOnce()
    {
        var stateDir = CreateTempDir();
        try
        {
            // No compaction model — _compactionChatClient should be null (not a separate instance).
            var mainClient = new Mock<IChatClient>();

            var service = CreateService(
                stateDir,
                chatClientFactory: _ => mainClient.Object,
                compactionModel: null);

            await service.ConnectAsync(TestContext.Current.CancellationToken);

            // _compactionChatClient should be null because no compaction model was configured.
            var compactionChatClient = GetField<IChatClient>(service, "_compactionChatClient");
            Assert.Null(compactionChatClient);

            await service.DisposeAsync();

            // The single chat client should be disposed exactly once.
            mainClient.Verify(c => c.Dispose(), Times.Once);

            Assert.Null(GetField<IChatClient>(service, "_chatClient"));
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── 8. AvailableModels live config projection ──

    [Fact]
    public void AvailableModels_WithHiveConfig_ProjectsCompositeStrings()
    {
        var stateDir = CreateTempDir();
        try
        {
            var hiveConfig = new HiveConfigFile
            {
                Models = new ModelsConfig
                {
                    AvailableModels =
                    [
                        new ModelEntry { Name = "model-with-effort", ReasoningEffort = "high" },
                        new ModelEntry { Name = "model-without-effort" },
                        new ModelEntry { Name = "model-empty-effort", ReasoningEffort = "" },
                    ]
                }
            };

            var service = CreateService(stateDir, hiveConfig: hiveConfig);

            var models = service.AvailableModels;

            Assert.Equal(3, models.Count);
            Assert.Equal("model-with-effort:high", models[0]);
            Assert.Equal("model-without-effort", models[1]);
            Assert.Equal("model-empty-effort", models[2]);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public void AvailableModels_WithoutHiveConfig_FallsBackToStartupModels()
    {
        var stateDir = CreateTempDir();
        try
        {
            var startupModels = new List<string> { "startup-a", "startup-b" }.AsReadOnly();

            var service = CreateService(
                stateDir,
                hiveConfig: null,
                startupAvailableModels: startupModels);

            var models = service.AvailableModels;

            Assert.Equal(2, models.Count);
            Assert.Equal("startup-a", models[0]);
            Assert.Equal("startup-b", models[1]);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public void AvailableModels_WithEmptyHiveConfigModels_FallsBackToStartupModels()
    {
        var stateDir = CreateTempDir();
        try
        {
            // HiveConfigFile present but Models.AvailableModels is null/empty.
            var hiveConfig = new HiveConfigFile
            {
                Models = new ModelsConfig
                {
                    AvailableModels = null
                }
            };

            var startupModels = new List<string> { "fallback-model" }.AsReadOnly();

            var service = CreateService(
                stateDir,
                hiveConfig: hiveConfig,
                startupAvailableModels: startupModels);

            var models = service.AvailableModels;

            Assert.Single(models);
            Assert.Equal("fallback-model", models[0]);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── Exceptional disposal paths ──

    [Fact]
    public async Task DisposeAsync_MainClientThrows_StillDisposesCompactionClientAndClearsState()
    {
        var stateDir = CreateTempDir();
        try
        {
            var mainClient = new Mock<IChatClient>();
            mainClient.Setup(c => c.Dispose()).Throws(new InvalidOperationException("boom"));
            var compactionClient = new Mock<IChatClient>();

            var service = CreateService(
                stateDir,
                chatClientFactory: m => m == "compaction-model" ? compactionClient.Object : mainClient.Object,
                compactionModel: "compaction-model");

            await service.ConnectAsync(TestContext.Current.CancellationToken);
            Assert.True(service.IsConnected);

            await Assert.ThrowsAsync<InvalidOperationException>(async () => await service.DisposeAsync());

            // The compaction client must still be disposed even though the main client threw.
            compactionClient.Verify(c => c.Dispose(), Times.Once);

            Assert.Null(GetField<IChatClient>(service, "_chatClient"));
            Assert.Null(GetField<IChatClient>(service, "_compactionChatClient"));
            Assert.Null(GetField<CodingAgent>(service, "_agent"));
            Assert.Null(GetField<AgentOptions>(service, "_agentOptions"));
            Assert.False(service.IsConnected);

            // Idempotent: a second dispose is a no-op and does not throw.
            await service.DisposeAsync();
            compactionClient.Verify(c => c.Dispose(), Times.Once);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task ConnectAsync_CompactionClientCreationThrows_DisposesMainClientAndClearsState()
    {
        var stateDir = CreateTempDir();
        try
        {
            var mainClient = new Mock<IChatClient>();

            var service = CreateService(
                stateDir,
                chatClientFactory: m => m == "compaction-model"
                    ? throw new InvalidOperationException("compaction client creation failed")
                    : mainClient.Object,
                compactionModel: "compaction-model");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ConnectAsync(TestContext.Current.CancellationToken));

            Assert.False(service.IsConnected);
            Assert.Null(GetField<IChatClient>(service, "_chatClient"));
            Assert.Null(GetField<IChatClient>(service, "_compactionChatClient"));
            Assert.Null(GetField<CodingAgent>(service, "_agent"));
            Assert.Null(GetField<AgentOptions>(service, "_agentOptions"));

            // The main client that was successfully created must not leak.
            mainClient.Verify(c => c.Dispose(), Times.Once);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task SwitchModelAsync_RecreateAgentThrows_DisposesClientsAndClearsState()
    {
        var stateDir = CreateTempDir();
        try
        {
            var firstClient = new Mock<IChatClient>();
            var callCount = 0;

            // First call (ConnectAsync) returns a real mock; the second call (SwitchModelAsync)
            // returns null so RecreateAgent fails with "Composer not connected".
            var service = CreateService(
                stateDir,
                chatClientFactory: _ => ++callCount == 1 ? firstClient.Object : null!,
                model: "test-model",
                startupAvailableModels: ["test-model", "other-model"]);

            await service.ConnectAsync(TestContext.Current.CancellationToken);
            Assert.True(service.IsConnected);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SwitchModelAsync("other-model"));

            Assert.False(service.IsConnected);
            Assert.Null(GetField<IChatClient>(service, "_chatClient"));
            Assert.Null(GetField<IChatClient>(service, "_compactionChatClient"));
            Assert.Null(GetField<CodingAgent>(service, "_agent"));
            Assert.Null(GetField<AgentOptions>(service, "_agentOptions"));

            // The originally connected client was disposed by the initial cleanup in SwitchModelAsync.
            firstClient.Verify(c => c.Dispose(), Times.Once);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task DisposeAsync_SameInstanceForMainAndCompaction_DisposesOnlyOnce()
    {
        var stateDir = CreateTempDir();
        try
        {
            var sharedClient = new Mock<IChatClient>();

            var service = CreateService(
                stateDir,
                chatClientFactory: _ => sharedClient.Object,
                compactionModel: "compaction-model");

            await service.ConnectAsync(TestContext.Current.CancellationToken);

            var main = GetField<IChatClient>(service, "_chatClient");
            var compaction = GetField<IChatClient>(service, "_compactionChatClient");
            Assert.Same(main, compaction);

            await service.DisposeAsync();

            sharedClient.Verify(c => c.Dispose(), Times.Once);
            Assert.Null(GetField<IChatClient>(service, "_chatClient"));
            Assert.Null(GetField<IChatClient>(service, "_compactionChatClient"));
            Assert.False(service.IsConnected);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── Utility ──

    private static void TryDeleteDir(string dir)
    {
        if (!Directory.Exists(dir))
            return;
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}