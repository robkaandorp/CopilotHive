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
using SharpCoder.SubAgents;

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
        ReasoningEffort? configuredReasoningEffort = null,
        string? compactionModel = null,
        LlmSessionRegistry? sessionRegistry = null,
        IReadOnlyList<string>? startupAvailableModels = null,
        IBrainRepoManager? repoManager = null,
        Action? onCompacting = null,
        Action<CompactionResult>? onCompacted = null,
        bool subAgentsEnabled = false,
        IReadOnlyList<ModelEntry>? subAgentModels = null,
        ILogger? logger = null,
        string? additionalImagesRoot = null)
    {
        return new ComposerAgentService(
            model,
            maxContextTokens,
            maxSteps,
            configuredReasoningEffort,
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
            onCompacted,
            subAgentsEnabled,
            // Default mirrors production: a construction-time snapshot taken from the config.
            subAgentModels ?? hiveConfig?.Models?.AvailableModels?.ToList().AsReadOnly() ?? [],
            additionalImagesRoot);
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

    // ── 1. AgentOptions getter throws before RecreateAgentAsync ──

    [Fact]
    public void AgentOptions_Getter_ThrowsBeforeRecreateAgentAsync()
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

    // ── 2. RecreateAgentAsync throws when not connected ──

    [Fact]
    public async Task RecreateAgentAsync_WhenNotConnected_ThrowsInvalidOperationException()
    {
        var stateDir = CreateTempDir();
        try
        {
            var service = CreateService(stateDir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await service.RecreateAgentAsync());
            Assert.Contains("Composer not connected", ex.Message);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── 3. ResetSessionAsync throws when not connected ──

    [Fact]
    public async Task ResetSessionAsync_WhenNotConnected_ThrowsInvalidOperationException()
    {
        var stateDir = CreateTempDir();
        try
        {
            var service = CreateService(stateDir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await service.ResetSessionAsync());
            Assert.Contains("Composer not connected", ex.Message);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── Disconnected shell: nullable model, no-model connect guard (Slice 1A2a) ──

    /// <summary>
    /// A <see cref="Func{T, TResult}"/> chat-client factory that COUNTS its invocations, so a
    /// test can prove no LLM client was ever created — not merely that the final field is null.
    /// A regression that calls the factory and discards the client would still be caught.
    /// </summary>
    private sealed class CountingChatClientFactory
    {
        private int _invocations;
        private readonly List<string> _requestedModels = [];
        private readonly object _lock = new();

        /// <summary>Number of times the factory delegate was invoked.</summary>
        public int Invocations => Volatile.Read(ref _invocations);

        /// <summary>The model identifiers the factory was asked to create clients for.</summary>
        public IReadOnlyList<string> RequestedModels
        {
            get { lock (_lock) { return _requestedModels.ToList(); } }
        }

        /// <summary>The delegate to hand to the service under test.</summary>
        public Func<string, IChatClient> Delegate => modelId =>
        {
            Interlocked.Increment(ref _invocations);
            lock (_lock) { _requestedModels.Add(modelId); }
            return new Mock<IChatClient>().Object;
        };
    }

    [Fact]
    public async Task ConnectAsync_NoModel_ThrowsNoModelConfigured_ClientFactoryNeverInvoked()
    {
        var stateDir = CreateTempDir();
        try
        {
            var factory = new CountingChatClientFactory();
            var service = CreateService(
                stateDir,
                chatClientFactory: factory.Delegate,
                model: null!);

            // Construction alone must never create a client.
            Assert.Equal(0, factory.Invocations);

            // No model configured → connect throws a clear error.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ConnectAsync(TestContext.Current.CancellationToken));
            Assert.Equal("no model configured", ex.Message);

            // The authoritative no-client assertion: the factory was NEVER invoked, so no LLM
            // client was created (and then discarded) anywhere along the path.
            Assert.Equal(0, factory.Invocations);
            Assert.Empty(factory.RequestedModels);

            Assert.False(service.IsConnected);
            Assert.Null(GetField<IChatClient>(service, "_chatClient"));
            Assert.Null(GetField<IChatClient>(service, "_compactionChatClient"));
            Assert.Null(GetField<CodingAgent>(service, "_agent"));
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task ConnectAsync_WhitespaceModel_ThrowsNoModelConfigured_ClientFactoryNeverInvoked()
    {
        var stateDir = CreateTempDir();
        try
        {
            var factory = new CountingChatClientFactory();
            var service = CreateService(
                stateDir,
                chatClientFactory: factory.Delegate,
                model: "   ");

            Assert.Equal(0, factory.Invocations);

            // Whitespace model normalizes to null → connect throws a clear error.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ConnectAsync(TestContext.Current.CancellationToken));
            Assert.Equal("no model configured", ex.Message);

            Assert.Equal(0, factory.Invocations);
            Assert.Empty(factory.RequestedModels);

            Assert.False(service.IsConnected);
            Assert.Null(GetField<IChatClient>(service, "_chatClient"));
            Assert.Null(GetField<IChatClient>(service, "_compactionChatClient"));
            Assert.Null(GetField<CodingAgent>(service, "_agent"));
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>
    /// Even with a compaction model configured, the no-model guard runs BEFORE any client
    /// creation — neither the main nor the compaction client is ever built.
    /// </summary>
    [Fact]
    public async Task ConnectAsync_NoModel_WithCompactionModel_ClientFactoryNeverInvoked()
    {
        var stateDir = CreateTempDir();
        try
        {
            var factory = new CountingChatClientFactory();
            var service = CreateService(
                stateDir,
                chatClientFactory: factory.Delegate,
                model: null!,
                compactionModel: "compaction-model");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ConnectAsync(TestContext.Current.CancellationToken));

            // NEITHER the main nor the compaction client may be created.
            Assert.Equal(0, factory.Invocations);
            Assert.DoesNotContain("compaction-model", factory.RequestedModels);
            Assert.Null(GetField<IChatClient>(service, "_compactionChatClient"));
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>
    /// Control test proving the counting factory actually observes client creation: with a
    /// configured model, <c>ConnectAsync</c> DOES invoke the factory. Without this, a factory
    /// that is never wired up would make the zero-invocation assertions vacuous.
    /// </summary>
    [Fact]
    public async Task ConnectAsync_WithModel_ClientFactoryInvokedForThatModel()
    {
        var stateDir = CreateTempDir();
        try
        {
            var factory = new CountingChatClientFactory();
            var service = CreateService(
                stateDir,
                chatClientFactory: factory.Delegate,
                model: "configured-model");

            await service.ConnectAsync(TestContext.Current.CancellationToken);

            Assert.True(service.IsConnected);
            Assert.Equal(1, factory.Invocations);
            Assert.Equal(["configured-model"], factory.RequestedModels);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public void Model_WithNullModel_IsNull()
    {
        var stateDir = CreateTempDir();
        try
        {
            var service = CreateService(stateDir, model: null!);
            Assert.Null(service.Model);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public void Model_WithWhitespaceModel_IsNull()
    {
        var stateDir = CreateTempDir();
        try
        {
            var service = CreateService(stateDir, model: " \t ");
            Assert.Null(service.Model);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public void AvailableModels_NoModel_StartupCatalogEmpty_NoNullEntry()
    {
        var stateDir = CreateTempDir();
        try
        {
            // No model and no startup catalog → EMPTY catalog, never a [null] entry.
            var service = CreateService(
                stateDir,
                model: null!,
                startupAvailableModels: []);

            Assert.Empty(service.AvailableModels);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task ConnectAsync_NoModel_RegistryNotWritten()
    {
        var stateDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var service = CreateService(
                stateDir,
                model: null!,
                sessionRegistry: registry);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ConnectAsync(TestContext.Current.CancellationToken));

            // No registry entry may carry a null model.
            Assert.Empty(registry.GetAll());
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task SwitchModelAsync_FromDisconnectedShell_ConnectsWithoutSessionLoad()
    {
        var stateDir = CreateTempDir();
        try
        {
            var factory = new CountingChatClientFactory();
            var service = CreateService(
                stateDir,
                chatClientFactory: factory.Delegate,
                model: null!,
                startupAvailableModels: ["model-a", "model-b"]);

            // Disconnected shell: no client, no agent, factory untouched.
            Assert.False(service.IsConnected);
            Assert.Null(GetField<IChatClient>(service, "_chatClient"));
            Assert.Equal(0, factory.Invocations);

            // Interim connect-on-select: SwitchModelAsync validates against the catalog and
            // creates client+agent WITHOUT a session disk-load.
            await service.SwitchModelAsync("model-b", ReasoningEffort.Medium, TestContext.Current.CancellationToken);

            Assert.True(service.IsConnected);
            Assert.Equal("model-b", service.Model);
            Assert.NotNull(GetField<IChatClient>(service, "_chatClient"));
            Assert.NotNull(GetField<CodingAgent>(service, "_agent"));

            // The client was created for the switch target only.
            Assert.Equal(1, factory.Invocations);
            Assert.Equal(["model-b"], factory.RequestedModels);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task SwitchModelAsync_RegistryUsesNewModel_NotNullableField()
    {
        var stateDir = CreateTempDir();
        try
        {
            var registry = new LlmSessionRegistry();
            var service = CreateService(
                stateDir,
                chatClientFactory: _ => new Mock<IChatClient>().Object,
                model: null!,
                startupAvailableModels: ["model-a", "model-b"],
                sessionRegistry: registry);

            await service.SwitchModelAsync("model-b", ReasoningEffort.Medium, TestContext.Current.CancellationToken);

            var sessions = registry.GetAll();
            var session = Assert.Single(sessions);
            Assert.Equal("model-b", session.Model);
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
            await service.SwitchModelAsync("model-b", ReasoningEffort.Medium, TestContext.Current.CancellationToken);

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

    // ── 6b. Configured reasoning effort across model switches ──

    private static HiveConfigFile TwoModelConfig() => new()
    {
        Models = new ModelsConfig
        {
            AvailableModels =
            [
                new ModelEntry { Name = "model-a" },
                new ModelEntry { Name = "model-b" },
                new ModelEntry { Name = "model-b:low" }
            ]
        }
    };

    [Fact]
    public async Task ConfiguredReasoningEffort_IsUsed_AtConstruction()
    {
        var stateDir = CreateTempDir();
        try
        {
            var mockClient = new Mock<IChatClient>();
            var service = CreateService(
                stateDir,
                chatClientFactory: _ => mockClient.Object,
                hiveConfig: TwoModelConfig(),
                model: "model-b:low",
                configuredReasoningEffort: ReasoningEffort.High,
                startupAvailableModels: ["model-b:low", "model-a"]);

            await using (service)
            {
                // The effective value is the configured one; the ':low' colon segment is part of the name.
                Assert.Equal(ReasoningEffort.High, service.ReasoningEffort);
                Assert.Equal(ReasoningEffort.High, GetField<ReasoningEffort?>(service, "_configuredReasoningEffort"));
            }
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>
    /// Without a configured value the reasoning effort stays unset — a ':low' segment in the
    /// model name is never a source of reasoning effort.
    /// </summary>
    [Fact]
    public async Task NoConfiguredReasoningEffort_LeavesReasoningUnset_AtConstruction()
    {
        var stateDir = CreateTempDir();
        try
        {
            var mockClient = new Mock<IChatClient>();
            var service = CreateService(
                stateDir,
                chatClientFactory: _ => mockClient.Object,
                hiveConfig: TwoModelConfig(),
                model: "model-b:low",
                startupAvailableModels: ["model-b:low", "model-a"]);

            await using (service)
            {
                Assert.Null(service.ReasoningEffort);
                Assert.Null(GetField<ReasoningEffort?>(service, "_configuredReasoningEffort"));
            }
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task SwitchModelAsync_AppliesSuppliedReasoningEffort_DoesNotReparseName()
    {
        var stateDir = CreateTempDir();
        try
        {
            var mockClient = new Mock<IChatClient>();
            var service = CreateService(
                stateDir,
                chatClientFactory: _ => mockClient.Object,
                hiveConfig: TwoModelConfig(),
                model: "model-a",
                configuredReasoningEffort: ReasoningEffort.High,
                startupAvailableModels: ["model-a", "model-b:low"]);

            await using (service)
            {
                await service.ConnectAsync(TestContext.Current.CancellationToken);
                Assert.Equal(ReasoningEffort.High, service.ReasoningEffort);

                // The new model name ends in ':low', but the explicitly supplied value wins.
                await service.SwitchModelAsync("model-b:low", ReasoningEffort.ExtraHigh, TestContext.Current.CancellationToken);

                Assert.Equal(ReasoningEffort.ExtraHigh, service.ReasoningEffort);
                Assert.Equal(ReasoningEffort.ExtraHigh, GetField<ReasoningEffort?>(service, "_configuredReasoningEffort"));
            }
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task SwitchModelAsync_NoneReasoningEffort_IsAppliedNotTreatedAsUnset()
    {
        var stateDir = CreateTempDir();
        try
        {
            var mockClient = new Mock<IChatClient>();
            var service = CreateService(
                stateDir,
                chatClientFactory: _ => mockClient.Object,
                hiveConfig: TwoModelConfig(),
                model: "model-a",
                configuredReasoningEffort: ReasoningEffort.High,
                startupAvailableModels: ["model-a", "model-b:low"]);

            await using (service)
            {
                await service.ConnectAsync(TestContext.Current.CancellationToken);

                await service.SwitchModelAsync("model-b:low", ReasoningEffort.None, TestContext.Current.CancellationToken);

                // None is an explicit level, not "unset" — it replaces the previous value.
                Assert.Equal(ReasoningEffort.None, service.ReasoningEffort);
                Assert.Equal(ReasoningEffort.None, GetField<ReasoningEffort?>(service, "_configuredReasoningEffort"));
            }
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task SwitchModelAsync_NoReasoningDerivedFromModelName_AcrossSwitches()
    {
        var stateDir = CreateTempDir();
        try
        {
            var mockClient = new Mock<IChatClient>();
            var service = CreateService(
                stateDir,
                chatClientFactory: _ => mockClient.Object,
                hiveConfig: TwoModelConfig(),
                model: "model-b:low",
                startupAvailableModels: ["model-b:low", "model-a"]);

            await using (service)
            {
                await service.ConnectAsync(TestContext.Current.CancellationToken);
                // No configured value → unset, even though the model name ends in ':low'.
                Assert.Null(service.ReasoningEffort);

                // The name carries no reasoning: only the supplied value is applied.
                await service.SwitchModelAsync("model-a", ReasoningEffort.Medium, TestContext.Current.CancellationToken);

                Assert.Equal(ReasoningEffort.Medium, service.ReasoningEffort);
                Assert.Equal(ReasoningEffort.Medium, GetField<ReasoningEffort?>(service, "_configuredReasoningEffort"));
            }
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

    /// <summary>
    /// The catalog exposes plain model names only — <c>reasoning_effort</c> is never appended.
    /// </summary>
    [Fact]
    public void AvailableModels_WithHiveConfig_ProjectsPlainModelNames()
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
            Assert.Equal("model-with-effort", models[0]);
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
    public void AvailableModels_WithEmptyHiveConfigModels_ReturnsEmptyNoFallback()
    {
        var stateDir = CreateTempDir();
        try
        {
            // HiveConfigFile present but Models.AvailableModels is null/empty: the config's
            // GetComposerAvailableModels() is the SOLE authority — an empty global list yields
            // an EMPTY catalog, never a fallback to the startup models.
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

            Assert.Empty(models);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── 8b. Catalog snapshot: ValidateAvailableModel / ApplyModelScalars (Slice 1A2b-2a) ──

    /// <summary>
    /// Membership is validated ordinal-ignore-case against ONE snapshot; the returned selection
    /// carries the caller's validated spelling, the passed-in reasoning effort, and the context
    /// window resolved from the SAME snapshot.
    /// </summary>
    [Fact]
    public void ValidateAvailableModel_MembershipOrdinalIgnoreCase_CarriesSpellingEffortAndContextWindow()
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
                        new ModelEntry { Name = "gpt-4", ContextWindow = 32768 },
                        new ModelEntry { Name = "claude-opus" }
                    ]
                }
            };

            var service = CreateService(stateDir, hiveConfig: hiveConfig, model: "gpt-4");

            // Case differs from the catalog spelling — membership is ordinal-ignore-case.
            var selection = service.ValidateAvailableModel("GPT-4", ReasoningEffort.High);

            Assert.Equal("GPT-4", selection.Model);          // caller's validated spelling
            Assert.Equal(ReasoningEffort.High, selection.ReasoningEffort);
            Assert.Equal(32768, selection.ContextWindow);    // from the same snapshot
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>
    /// An invalid candidate throws the SAME <see cref="ArgumentException"/> (type, message, and
    /// <c>ParamName</c>) as the pre-refactor switch.
    /// </summary>
    [Fact]
    public void ValidateAvailableModel_InvalidCandidate_ThrowsSameArgumentException()
    {
        var stateDir = CreateTempDir();
        try
        {
            var service = CreateService(
                stateDir,
                model: "model-a",
                startupAvailableModels: ["model-a", "model-b"]);

            var ex = Assert.Throws<ArgumentException>(
                () => service.ValidateAvailableModel("unknown-model", ReasoningEffort.Medium));

            Assert.Equal("newModel", ex.ParamName);
            Assert.Equal(
                "Model 'unknown-model' is not available. Available models: model-a, model-b.",
                ex.Message.Replace(" (Parameter 'newModel')", ""));
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>
    /// A whitespace-only/blank candidate fails membership exactly as today — there is NO input
    /// trimming (that is Slice 3).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t ")]
    public void ValidateAvailableModel_BlankOrWhitespaceCandidate_FailsMembership(string candidate)
    {
        var stateDir = CreateTempDir();
        try
        {
            var service = CreateService(
                stateDir,
                model: "model-a",
                startupAvailableModels: ["model-a", "model-b"]);

            var ex = Assert.Throws<ArgumentException>(
                () => service.ValidateAvailableModel(candidate, ReasoningEffort.Medium));

            Assert.Equal("newModel", ex.ParamName);
            Assert.Contains($"Model '{candidate}' is not available", ex.Message);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>
    /// <see cref="ComposerAgentService.ValidateAvailableModel"/> never mutates state — not on
    /// success, not on failure.
    /// </summary>
    [Fact]
    public void ValidateAvailableModel_DoesNotMutateState()
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
                        new ModelEntry { Name = "model-a", ContextWindow = 32768 },
                        new ModelEntry { Name = "model-b" }
                    ]
                }
            };

            var service = CreateService(
                stateDir,
                hiveConfig: hiveConfig,
                model: "model-a",
                maxContextTokens: 64000,
                configuredReasoningEffort: ReasoningEffort.Low);

            // Success path: no mutation.
            var selection = service.ValidateAvailableModel("model-b", ReasoningEffort.High);
            Assert.Equal("model-b", selection.Model);
            Assert.Equal(64000, service.MaxContextTokens);
            Assert.Equal("model-a", service.Model);
            Assert.Equal(ReasoningEffort.Low, service.ReasoningEffort);

            // Failure path: no mutation either.
            Assert.Throws<ArgumentException>(
                () => service.ValidateAvailableModel("unknown", ReasoningEffort.High));
            Assert.Equal(64000, service.MaxContextTokens);
            Assert.Equal("model-a", service.Model);
            Assert.Equal(ReasoningEffort.Low, service.ReasoningEffort);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>
    /// ONE-snapshot stability: the snapshot is captured ONCE during validation; mutating
    /// <c>HiveConfigFile.Models</c> between <c>ValidateAvailableModel</c> and
    /// <c>ApplyModelScalars</c> cannot change the applied context window (mutation test, no
    /// read-counting instrumentation).
    /// </summary>
    [Fact]
    public void ValidateAvailableModel_ThenApplyModelScalars_UsesOriginalSnapshot_ConfigMutatedBetween()
    {
        var stateDir = CreateTempDir();
        try
        {
            var hiveConfig = new HiveConfigFile
            {
                Models = new ModelsConfig
                {
                    AvailableModels = [new ModelEntry { Name = "gpt-4", ContextWindow = 32768 }]
                }
            };

            var service = CreateService(
                stateDir,
                hiveConfig: hiveConfig,
                model: "gpt-4",
                maxContextTokens: 64000);

            var selection = service.ValidateAvailableModel("gpt-4", ReasoningEffort.Medium);
            Assert.Equal(32768, selection.ContextWindow);

            // MUTATE the live config between validation and application: a live re-read would
            // now resolve 50000 — the applied value must come from the ORIGINAL snapshot.
            hiveConfig.Models!.AvailableModels![0].ContextWindow = 50000;

            service.ApplyModelScalars(selection);

            Assert.Equal("gpt-4", service.Model);
            Assert.Equal(ReasoningEffort.Medium, service.ReasoningEffort);
            Assert.Equal(32768, service.MaxContextTokens);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>
    /// Snapshot normalization: catalog entries with surrounding whitespace normalize to their
    /// trimmed name WITH the entry's context window; ordinal-ignore-case duplicates collapse to
    /// the FIRST; empty/whitespace entries are dropped.
    /// </summary>
    [Fact]
    public void ValidateAvailableModel_NormalizesCatalogEntries_TrimsDropsDuplicatesFirstWins()
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
                        new ModelEntry { Name = "  GPT-4  ", ContextWindow = 32768 },
                        new ModelEntry { Name = "gpt-4", ContextWindow = 99999 },   // duplicate — FIRST wins
                        new ModelEntry { Name = "   " },                            // whitespace — dropped
                        new ModelEntry { Name = "" },                               // empty — dropped
                        new ModelEntry { Name = "claude-opus", ContextWindow = null }
                    ]
                }
            };

            var service = CreateService(stateDir, hiveConfig: hiveConfig, model: "GPT-4");

            // The whitespace-wrapped catalog name resolves to its trimmed form with the FIRST
            // entry's context window (32768, not the duplicate's 99999).
            var selection = service.ValidateAvailableModel("gpt-4", ReasoningEffort.Medium);
            Assert.Equal("gpt-4", selection.Model);
            Assert.Equal(32768, selection.ContextWindow);

            // An entry with no context window resolves to null.
            var noCtx = service.ValidateAvailableModel("claude-opus", ReasoningEffort.Medium);
            Assert.Equal("claude-opus", noCtx.Model);
            Assert.Null(noCtx.ContextWindow);

            // A candidate carrying whitespace does NOT match the trimmed catalog entry (no
            // input trimming — that is Slice 3).
            Assert.Throws<ArgumentException>(
                () => service.ValidateAvailableModel("  gpt-4  ", ReasoningEffort.Medium));
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>
    /// Null-config seam: when <c>_hiveConfig == null</c> the snapshot derives from
    /// <c>_startupAvailableModels</c>, normalized (trimmed, deduped, blanks dropped), with
    /// <c>ContextWindow = null</c> for every entry.
    /// </summary>
    [Fact]
    public void ValidateAvailableModel_NullConfig_UsesStartupCatalog_NormalizedNullContextWindow()
    {
        var stateDir = CreateTempDir();
        try
        {
            var service = CreateService(
                stateDir,
                hiveConfig: null,
                model: "model-a",
                startupAvailableModels: ["  model-a  ", "MODEL-A", "model-b", "   "]);

            // A non-normalized startup entry normalizes; membership is ordinal-ignore-case.
            var selection = service.ValidateAvailableModel("model-a", ReasoningEffort.High);
            Assert.Equal("model-a", selection.Model);
            Assert.Null(selection.ContextWindow);   // explicit null-config rule

            // The case-variant duplicate was collapsed by the snapshot normalization.
            var upper = service.ValidateAvailableModel("MODEL-A", ReasoningEffort.High);
            Assert.Equal("MODEL-A", upper.Model);
            Assert.Null(upper.ContextWindow);

            // The whitespace-only startup entry is dropped — it is not selectable.
            Assert.Throws<ArgumentException>(
                () => service.ValidateAvailableModel("   ", ReasoningEffort.High));
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>
    /// <c>ApplyModelScalars</c> assigns model + reasoning, and updates <c>_maxContextTokens</c>
    /// only when the snapshot context is positive — emitting the existing context-window log
    /// with the same condition/values/ordering.
    /// </summary>
    [Fact]
    public void ApplyModelScalars_PositiveContextWindow_UpdatesMaxTokens_EmitsLog()
    {
        var stateDir = CreateTempDir();
        try
        {
            var logger = new TestLogger<ComposerAgentService>();
            var service = CreateService(
                stateDir,
                model: "old-model",
                maxContextTokens: 64000,
                configuredReasoningEffort: ReasoningEffort.Low,
                logger: logger);

            service.ApplyModelScalars(new ValidatedModelSelection("new-model", ReasoningEffort.Medium, 32768));

            Assert.Equal("new-model", service.Model);
            Assert.Equal(ReasoningEffort.Medium, service.ReasoningEffort);
            Assert.Equal(ReasoningEffort.Medium, GetField<ReasoningEffort?>(service, "_configuredReasoningEffort"));
            Assert.Equal(32768, service.MaxContextTokens);

            var log = Assert.Single(logger.LogEntries);
            Assert.Equal(LogLevel.Information, log.LogLevel);
            Assert.Equal(
                "Updating Composer context window from 64000 to 32768 for model 'new-model'",
                log.Message);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>
    /// <c>ApplyModelScalars</c> keeps the current <c>_maxContextTokens</c> (and emits NO
    /// context-window log) when the snapshot context is null, non-positive, or equal to the
    /// current value — the conditional update rule is preserved exactly.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void ApplyModelScalars_ContextNullOrNonPositive_KeepsMaxTokens_NoLog(int? contextWindow)
    {
        var stateDir = CreateTempDir();
        try
        {
            var logger = new TestLogger<ComposerAgentService>();
            var service = CreateService(
                stateDir,
                model: "old-model",
                maxContextTokens: 64000,
                configuredReasoningEffort: ReasoningEffort.Low,
                logger: logger);

            service.ApplyModelScalars(new ValidatedModelSelection("new-model", ReasoningEffort.Medium, contextWindow));

            Assert.Equal("new-model", service.Model);
            Assert.Equal(ReasoningEffort.Medium, service.ReasoningEffort);
            Assert.Equal(64000, service.MaxContextTokens);
            Assert.Empty(logger.LogEntries);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public void ApplyModelScalars_ContextWindowEqualToCurrent_KeepsMaxTokens_NoLog()
    {
        var stateDir = CreateTempDir();
        try
        {
            var logger = new TestLogger<ComposerAgentService>();
            var service = CreateService(
                stateDir,
                model: "old-model",
                maxContextTokens: 32768,
                logger: logger);

            service.ApplyModelScalars(new ValidatedModelSelection("new-model", ReasoningEffort.Medium, 32768));

            Assert.Equal(32768, service.MaxContextTokens);
            Assert.Empty(logger.LogEntries);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>
    /// Regression: an invalid candidate still throws BEFORE any teardown — the connected
    /// client, agent, and session survive a rejected switch untouched (no disposal, no
    /// scalar mutation).
    /// </summary>
    [Fact]
    public async Task SwitchModelAsync_InvalidModel_ThrowsBeforeAnyTeardown()
    {
        var stateDir = CreateTempDir();
        try
        {
            var mockClient = new Mock<IChatClient>();
            var service = CreateService(
                stateDir,
                chatClientFactory: _ => mockClient.Object,
                model: "model-a",
                maxContextTokens: 64000,
                startupAvailableModels: ["model-a", "model-b"]);

            await service.ConnectAsync(TestContext.Current.CancellationToken);
            var oldClient = GetField<IChatClient>(service, "_chatClient");
            var oldAgent = service.Agent;
            var oldSession = service.Session;
            Assert.NotNull(oldClient);
            Assert.NotNull(oldAgent);

            await Assert.ThrowsAsync<ArgumentException>(
                () => service.SwitchModelAsync("unknown-model", ReasoningEffort.High, TestContext.Current.CancellationToken));

            // No teardown: same client and agent instances, still connected.
            Assert.Same(oldClient, GetField<IChatClient>(service, "_chatClient"));
            Assert.Same(oldAgent, service.Agent);
            Assert.True(service.IsConnected);
            Assert.Same(oldSession, service.Session);

            // No scalar mutation: model, reasoning, and context window are untouched.
            Assert.Equal("model-a", service.Model);
            Assert.Equal(64000, service.MaxContextTokens);
            Assert.Null(service.ReasoningEffort);

            // The client was never disposed.
            mockClient.Verify(c => c.Dispose(), Times.Never);
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
    public async Task SwitchModelAsync_RecreateAgentAsyncThrows_DisposesClientsAndClearsState()
    {
        var stateDir = CreateTempDir();
        try
        {
            var firstClient = new Mock<IChatClient>();
            var callCount = 0;

            // First call (ConnectAsync) returns a real mock; the second call (SwitchModelAsync)
            // returns null so RecreateAgentAsync fails with "Composer not connected".
            var service = CreateService(
                stateDir,
                chatClientFactory: _ => ++callCount == 1 ? firstClient.Object : null!,
                model: "test-model",
                startupAvailableModels: ["test-model", "other-model"]);

            await service.ConnectAsync(TestContext.Current.CancellationToken);
            Assert.True(service.IsConnected);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SwitchModelAsync("other-model", ReasoningEffort.Medium, TestContext.Current.CancellationToken));

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

    // ── Sub-Agent enablement tests ──

    // ── 9. SubAgents enabled when models configured AND repo manager present ──

    [Fact]
    public async Task SubAgents_EnabledWhenModelsConfiguredAndRepoManagerPresent_NonNullWithCorrectValues()
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
                        new ModelEntry { Name = "gpt-4" },
                        new ModelEntry { Name = "claude-3", ContextWindow = 200000 },
                    ]
                }
            };

            var repoManager = new Mock<IBrainRepoManager>();
            repoManager.SetupGet(r => r.WorkDirectory).Returns(stateDir);

            var mockClient = new Mock<IChatClient>();

            var service = CreateService(
                stateDir,
                chatClientFactory: _ => mockClient.Object,
                hiveConfig: hiveConfig,
                repoManager: repoManager.Object,
                subAgentsEnabled: true);

            await service.ConnectAsync(TestContext.Current.CancellationToken);

            var subAgents = service.AgentOptions.SubAgents;
            Assert.NotNull(subAgents);

            // AvailableModels mapped via SubAgentModelInfo constructor
            Assert.Equal(2, subAgents!.AvailableModels.Count);
            Assert.Equal("gpt-4", subAgents.AvailableModels[0].Id);
            Assert.Equal("Configured model", subAgents.AvailableModels[0].Description);
            Assert.Null(subAgents.AvailableModels[0].ContextWindow);

            Assert.Equal("claude-3", subAgents.AvailableModels[1].Id);
            Assert.Contains("200K context window", subAgents.AvailableModels[1].Description!);
            Assert.Equal(200000, subAgents.AvailableModels[1].ContextWindow);

            // Scalar options
            Assert.Equal(4, subAgents.MaxConcurrentSubAgents);
            Assert.Equal(TimeSpan.FromMinutes(5), subAgents.DefaultTimeout);
            Assert.Equal(TimeSpan.FromMinutes(15), subAgents.MaxTimeout);
            Assert.Equal(8_000, subAgents.MaxSummaryChars);

            // ClientFactory is set
            Assert.NotNull(subAgents.ClientFactory);

            // Default permission flags
            Assert.True(subAgents.DefaultEnableFileOps);
            Assert.False(subAgents.DefaultEnableBash);
            Assert.False(subAgents.DefaultEnableFileWrites);
            Assert.False(subAgents.DefaultEnableSkills);

            await service.DisposeAsync();
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── 10. SubAgents disabled when models empty ──

    [Fact]
    public async Task SubAgents_DisabledWhenModelsEmpty_ReturnsNull()
    {
        var stateDir = CreateTempDir();
        try
        {
            var hiveConfig = new HiveConfigFile
            {
                Models = new ModelsConfig
                {
                    AvailableModels = []
                }
            };

            var repoManager = new Mock<IBrainRepoManager>();
            repoManager.SetupGet(r => r.WorkDirectory).Returns(stateDir);

            var mockClient = new Mock<IChatClient>();

            var service = CreateService(
                stateDir,
                chatClientFactory: _ => mockClient.Object,
                hiveConfig: hiveConfig,
                repoManager: repoManager.Object,
                subAgentsEnabled: true);

            await service.ConnectAsync(TestContext.Current.CancellationToken);

            Assert.Null(service.AgentOptions.SubAgents);

            await service.DisposeAsync();
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── 11. SubAgents disabled when repo manager null ──

    [Fact]
    public async Task SubAgents_DisabledWhenRepoManagerNull_ReturnsNull()
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
                        new ModelEntry { Name = "gpt-4" },
                    ]
                }
            };

            var mockClient = new Mock<IChatClient>();

            var service = CreateService(
                stateDir,
                chatClientFactory: _ => mockClient.Object,
                hiveConfig: hiveConfig,
                repoManager: null,
                subAgentsEnabled: true);

            await service.ConnectAsync(TestContext.Current.CancellationToken);

            Assert.Null(service.AgentOptions.SubAgents);

            await service.DisposeAsync();
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── SupportsVision mapping (nullable source → ?? false) ──────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task SubAgents_MapsSupportsVision_NullableSourceResolvesToFalseWhenNull(bool? vision)
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
                        new ModelEntry { Name = "gpt-4", ContextWindow = 200000, SupportsVision = vision },
                    ]
                }
            };

            var repoManager = new Mock<IBrainRepoManager>();
            repoManager.SetupGet(r => r.WorkDirectory).Returns(stateDir);

            var mockClient = new Mock<IChatClient>();

            var service = CreateService(
                stateDir,
                chatClientFactory: _ => mockClient.Object,
                hiveConfig: hiveConfig,
                repoManager: repoManager.Object,
                subAgentsEnabled: true);

            await service.ConnectAsync(TestContext.Current.CancellationToken);

            var subAgents = service.AgentOptions.SubAgents;
            Assert.NotNull(subAgents);
            // Composer maps nullable source as entry.SupportsVision ?? false
            Assert.Equal(vision ?? false, subAgents!.AvailableModels[0].SupportsVision);

            await service.DisposeAsync();
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── 12. SubAgents disabled when flag is false ──

    [Fact]
    public async Task SubAgents_DisabledWhenFlagFalse_ReturnsNull()
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
                        new ModelEntry { Name = "gpt-4" },
                    ]
                }
            };

            var repoManager = new Mock<IBrainRepoManager>();
            repoManager.SetupGet(r => r.WorkDirectory).Returns(stateDir);

            var mockClient = new Mock<IChatClient>();

            var service = CreateService(
                stateDir,
                chatClientFactory: _ => mockClient.Object,
                hiveConfig: hiveConfig,
                repoManager: repoManager.Object,
                subAgentsEnabled: false);

            await service.ConnectAsync(TestContext.Current.CancellationToken);

            Assert.Null(service.AgentOptions.SubAgents);

            await service.DisposeAsync();
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── 13. ClientFactory delegates to CreateClient (injected chatClientFactory) ──

    [Fact]
    public async Task SubAgents_ClientFactory_DelegatesToInjectedChatClientFactory()
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
                        new ModelEntry { Name = "test-model" },
                    ]
                }
            };

            var repoManager = new Mock<IBrainRepoManager>();
            repoManager.SetupGet(r => r.WorkDirectory).Returns(stateDir);

            var factoryCalls = new List<string>();
            Func<string, IChatClient> factory = id =>
            {
                factoryCalls.Add(id);
                return new Mock<IChatClient>().Object;
            };

            var service = CreateService(
                stateDir,
                chatClientFactory: factory,
                hiveConfig: hiveConfig,
                repoManager: repoManager.Object,
                subAgentsEnabled: true);

            // ConnectAsync calls CreateClient for the main model.
            await service.ConnectAsync(TestContext.Current.CancellationToken);

            var subAgents = service.AgentOptions.SubAgents;
            Assert.NotNull(subAgents);

            // Invoke the sub-agent ClientFactory with a different model ID.
            subAgents!.ClientFactory!("sub-agent-model");

            // The factory must have been called with "sub-agent-model" — proving
            // the ClientFactory delegates to the injected chatClientFactory (CreateClient),
            // NOT to ChatClientFactory.Create directly.
            Assert.Contains("sub-agent-model", factoryCalls);

            // At least 2 calls: one for ConnectAsync (main model), one for the sub-agent.
            Assert.True(factoryCalls.Count >= 2,
                $"Expected at least 2 factory calls, got {factoryCalls.Count}");

            await service.DisposeAsync();
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── 14. SubAgentsSystemPromptSection present when enabled, absent when disabled ──

    [Fact]
    public void Composer_SubAgentsSystemPromptSection_PresentWhenEnabled()
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
                        new ModelEntry { Name = "gpt-4" },
                    ]
                }
            };

            var repoManager = new Mock<IBrainRepoManager>();
            repoManager.SetupGet(r => r.WorkDirectory).Returns(stateDir);

            using var dbContext = CopilotHive.Persistence.CopilotHiveDbContext.CreateInMemory();
            var store = new CopilotHive.Goals.GoalStore(dbContext, Microsoft.Extensions.Logging.Abstractions.NullLogger<CopilotHive.Goals.GoalStore>.Instance);

            var composer = new Composer(
                "test-model",
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Composer>.Instance,
                store,
                repoManager: repoManager.Object,
                stateDir: stateDir,
                hiveConfig: hiveConfig);

            var prompt = composer.GetSystemPrompt();
            Assert.Contains("Sub-Agents", prompt);
            Assert.Contains("start_sub_agent", prompt);
            Assert.Contains("list_sub_agent_models", prompt);
            Assert.Contains("read-only", prompt);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public void Composer_SubAgentsSystemPromptSection_UsesTaskParameterAndDocumentsVisionDelegation()
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
                        new ModelEntry { Name = "gpt-4o", SupportsVision = true },
                    ]
                }
            };

            var repoManager = new Mock<IBrainRepoManager>();
            repoManager.SetupGet(r => r.WorkDirectory).Returns(stateDir);

            using var dbContext = CopilotHive.Persistence.CopilotHiveDbContext.CreateInMemory();
            var store = new CopilotHive.Goals.GoalStore(dbContext, Microsoft.Extensions.Logging.Abstractions.NullLogger<CopilotHive.Goals.GoalStore>.Instance);

            var composer = new Composer(
                "test-model",
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Composer>.Instance,
                store,
                repoManager: repoManager.Object,
                stateDir: stateDir,
                hiveConfig: hiveConfig);

            var prompt = composer.GetSystemPrompt();

            // The first parameter is task, not prompt (bug fix from the previous doc).
            Assert.Contains("start_sub_agent(task, model?, timeout_seconds?, image_paths?)", prompt);
            Assert.DoesNotContain("start_sub_agent(prompt, model?, timeout_seconds?)", prompt);

            // Vision delegation guidance must be present with a valid example.
            Assert.Contains("Vision delegation:", prompt);
            Assert.Contains("image_paths:", prompt);
            Assert.Contains("supports_vision: true", prompt);
            Assert.Contains("start_sub_agent(task: \"Analyze this attachment and describe what you see\", image_paths: [\"<attachment path>\"])", prompt);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public void Composer_SubAgentsSystemPromptSection_AbsentWhenDisabled()
    {
        var stateDir = CreateTempDir();
        try
        {
            // No models configured → sub-agents disabled.
            var hiveConfig = new HiveConfigFile
            {
                Models = new ModelsConfig
                {
                    AvailableModels = []
                }
            };

            using var dbContext = CopilotHive.Persistence.CopilotHiveDbContext.CreateInMemory();
            var store = new CopilotHive.Goals.GoalStore(dbContext, Microsoft.Extensions.Logging.Abstractions.NullLogger<CopilotHive.Goals.GoalStore>.Instance);

            // No repo manager → sub-agents disabled.
            var composer = new Composer(
                "test-model",
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Composer>.Instance,
                store,
                repoManager: null,
                stateDir: stateDir,
                hiveConfig: hiveConfig);

            var prompt = composer.GetSystemPrompt();
            Assert.DoesNotContain("Sub-Agents", prompt);
            Assert.DoesNotContain("start_sub_agent", prompt);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public void Composer_SubAgentsSystemPromptSection_AbsentWhenNoRepoManager()
    {
        var stateDir = CreateTempDir();
        try
        {
            // Models configured but no repo manager → sub-agents disabled.
            var hiveConfig = new HiveConfigFile
            {
                Models = new ModelsConfig
                {
                    AvailableModels =
                    [
                        new ModelEntry { Name = "gpt-4" },
                    ]
                }
            };

            using var dbContext = CopilotHive.Persistence.CopilotHiveDbContext.CreateInMemory();
            var store = new CopilotHive.Goals.GoalStore(dbContext, Microsoft.Extensions.Logging.Abstractions.NullLogger<CopilotHive.Goals.GoalStore>.Instance);

            var composer = new Composer(
                "test-model",
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Composer>.Instance,
                store,
                repoManager: null,
                stateDir: stateDir,
                hiveConfig: hiveConfig);

            var prompt = composer.GetSystemPrompt();
            Assert.DoesNotContain("Sub-Agents", prompt);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── Disposal: OnAgentDisposing hook fires on all FIVE existing-agent paths ──

    [Fact]
    public async Task OnAgentDisposing_FiresOnConnectAsyncReconnect()
    {
        var stateDir = CreateTempDir();
        try
        {
            var mockClient = new Mock<IChatClient>();
            var service = CreateService(stateDir, chatClientFactory: _ => mockClient.Object);
            await service.ConnectAsync(TestContext.Current.CancellationToken);
            var oldAgent = service.Agent;
            Assert.NotNull(oldAgent);

            var capturedAgents = new List<CodingAgent>();
            service.OnAgentDisposing = agent => capturedAgents.Add(agent);

            // Reconnect — should dispose the old agent.
            await service.ConnectAsync(TestContext.Current.CancellationToken);

            Assert.Single(capturedAgents);
            Assert.Same(oldAgent, capturedAgents[0]);

            await service.DisposeAsync();
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task OnAgentDisposing_FiresOnSwitchModelAsync()
    {
        var stateDir = CreateTempDir();
        try
        {
            var mockClient = new Mock<IChatClient>();
            var service = CreateService(
                stateDir,
                chatClientFactory: _ => mockClient.Object,
                model: "model-a",
                startupAvailableModels: ["model-a", "model-b"]);
            await service.ConnectAsync(TestContext.Current.CancellationToken);
            var oldAgent = service.Agent;
            Assert.NotNull(oldAgent);

            var capturedAgents = new List<CodingAgent>();
            service.OnAgentDisposing = agent => capturedAgents.Add(agent);

            await service.SwitchModelAsync("model-b", ReasoningEffort.Medium, TestContext.Current.CancellationToken);

            Assert.Single(capturedAgents);
            Assert.Same(oldAgent, capturedAgents[0]);

            await service.DisposeAsync();
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task OnAgentDisposing_FiresOnRecreateAgentAsync()
    {
        var stateDir = CreateTempDir();
        try
        {
            var mockClient = new Mock<IChatClient>();
            var service = CreateService(stateDir, chatClientFactory: _ => mockClient.Object);
            await service.ConnectAsync(TestContext.Current.CancellationToken);
            var oldAgent = service.Agent;
            Assert.NotNull(oldAgent);

            var capturedAgents = new List<CodingAgent>();
            service.OnAgentDisposing = agent => capturedAgents.Add(agent);

            await service.RecreateAgentAsync();

            Assert.Single(capturedAgents);
            Assert.Same(oldAgent, capturedAgents[0]);

            await service.DisposeAsync();
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task OnAgentDisposing_FiresOnResetSessionAsync()
    {
        var stateDir = CreateTempDir();
        try
        {
            var mockClient = new Mock<IChatClient>();
            var service = CreateService(stateDir, chatClientFactory: _ => mockClient.Object);
            await service.ConnectAsync(TestContext.Current.CancellationToken);
            var oldAgent = service.Agent;
            Assert.NotNull(oldAgent);

            var capturedAgents = new List<CodingAgent>();
            service.OnAgentDisposing = agent => capturedAgents.Add(agent);

            await service.ResetSessionAsync();

            Assert.Single(capturedAgents);
            Assert.Same(oldAgent, capturedAgents[0]);

            await service.DisposeAsync();
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task OnAgentDisposing_FiresOnDisposeAsync()
    {
        var stateDir = CreateTempDir();
        try
        {
            var mockClient = new Mock<IChatClient>();
            var service = CreateService(stateDir, chatClientFactory: _ => mockClient.Object);
            await service.ConnectAsync(TestContext.Current.CancellationToken);
            var oldAgent = service.Agent;
            Assert.NotNull(oldAgent);

            var capturedAgents = new List<CodingAgent>();
            service.OnAgentDisposing = agent => capturedAgents.Add(agent);

            await service.DisposeAsync();

            Assert.Single(capturedAgents);
            Assert.Same(oldAgent, capturedAgents[0]);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── OnAgentDisposing does NOT fire when no old agent exists (failed-creation cleanup) ──

    [Fact]
    public async Task OnAgentDisposing_NotFiredWhenNoOldAgent_FailedCreationCleanup()
    {
        var stateDir = CreateTempDir();
        try
        {
            var service = CreateService(
                stateDir,
                chatClientFactory: _ => throw new InvalidOperationException("client creation failed"));

            var hookCalled = false;
            service.OnAgentDisposing = _ => hookCalled = true;

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ConnectAsync(TestContext.Current.CancellationToken));

            // No old agent existed, so the hook must NOT fire.
            Assert.False(hookCalled);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── Agent-only paths retain the client ──

    [Fact]
    public async Task RecreateAgentAsync_RetainsChatClient_NotDisposed()
    {
        var stateDir = CreateTempDir();
        try
        {
            var mockClient = new Mock<IChatClient>();
            var service = CreateService(stateDir, chatClientFactory: _ => mockClient.Object);
            await service.ConnectAsync(TestContext.Current.CancellationToken);

            var originalClient = GetField<IChatClient>(service, "_chatClient");
            Assert.NotNull(originalClient);

            await service.RecreateAgentAsync();

            // Client must NOT be disposed (agent-only path retains client).
            mockClient.Verify(c => c.Dispose(), Times.Never);

            // The same client reference must still be present.
            Assert.Same(originalClient, GetField<IChatClient>(service, "_chatClient"));

            await service.DisposeAsync();
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task ResetSessionAsync_RetainsChatClient_NotDisposed()
    {
        var stateDir = CreateTempDir();
        try
        {
            var mockClient = new Mock<IChatClient>();
            var service = CreateService(stateDir, chatClientFactory: _ => mockClient.Object);
            await service.ConnectAsync(TestContext.Current.CancellationToken);

            var originalClient = GetField<IChatClient>(service, "_chatClient");
            Assert.NotNull(originalClient);

            await service.ResetSessionAsync();

            // Client must NOT be disposed (agent-only path retains client).
            mockClient.Verify(c => c.Dispose(), Times.Never);

            // The same client reference must still be present.
            Assert.Same(originalClient, GetField<IChatClient>(service, "_chatClient"));

            await service.DisposeAsync();
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── Client-replacing paths dispose agent BEFORE client ──

    [Fact]
    public async Task SwitchModelAsync_DisposesAgentBeforeClient()
    {
        var stateDir = CreateTempDir();
        try
        {
            var firstClient = new Mock<IChatClient>();
            var disposalOrder = new List<string>();

            // Record client disposal order via callback.
            // IChatClient extends IDisposable (not IAsyncDisposable), so Dispose() is called.
            firstClient.Setup(c => c.Dispose())
                .Callback(() => disposalOrder.Add("client"));

            var service = CreateService(
                stateDir,
                chatClientFactory: _ => firstClient.Object,
                model: "model-a",
                startupAvailableModels: ["model-a", "model-b"]);
            await service.ConnectAsync(TestContext.Current.CancellationToken);

            // Record agent disposal via hook.
            service.OnAgentDisposing = _ => disposalOrder.Add("agent");

            await service.SwitchModelAsync("model-b", ReasoningEffort.Medium, TestContext.Current.CancellationToken);

            // Agent must be disposed BEFORE client.
            Assert.Equal(2, disposalOrder.Count);
            Assert.Equal("agent", disposalOrder[0]);
            Assert.Equal("client", disposalOrder[1]);

            await service.DisposeAsync();
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task DisposeAsync_DisposesAgentBeforeClient()
    {
        var stateDir = CreateTempDir();
        try
        {
            var mockClient = new Mock<IChatClient>();
            var disposalOrder = new List<string>();

            mockClient.Setup(c => c.Dispose())
                .Callback(() => disposalOrder.Add("client"));

            var service = CreateService(stateDir, chatClientFactory: _ => mockClient.Object);
            await service.ConnectAsync(TestContext.Current.CancellationToken);

            service.OnAgentDisposing = _ => disposalOrder.Add("agent");

            await service.DisposeAsync();

            // Agent must be disposed BEFORE client.
            Assert.Equal(2, disposalOrder.Count);
            Assert.Equal("agent", disposalOrder[0]);
            Assert.Equal("client", disposalOrder[1]);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── Hook-throwing case on a client-replacing path ──

    [Fact]
    public async Task SwitchModelAsync_HookThrows_StillDisposesAgentAndClient()
    {
        var stateDir = CreateTempDir();
        try
        {
            var hiveConfig = new HiveConfigFile
            {
                Models = new ModelsConfig
                {
                    AvailableModels = [new ModelEntry { Name = "model-a" }, new ModelEntry { Name = "model-b" }]
                }
            };

            var repoManager = new Mock<IBrainRepoManager>();
            repoManager.SetupGet(r => r.WorkDirectory).Returns(stateDir);

            var firstClient = new Mock<IChatClient>();
            var clientDisposed = false;
            firstClient.Setup(c => c.Dispose())
                .Callback(() => clientDisposed = true);

            var callCount = 0;
            var service = CreateService(
                stateDir,
                chatClientFactory: _ => ++callCount == 1 ? firstClient.Object : new Mock<IChatClient>().Object,
                hiveConfig: hiveConfig,
                model: "model-a",
                startupAvailableModels: ["model-a", "model-b"],
                repoManager: repoManager.Object,
                subAgentsEnabled: true);
            await service.ConnectAsync(TestContext.Current.CancellationToken);

            // Force creation of the lazy SubAgentManager on the CURRENT agent so we hold a
            // live handle that can prove the agent was really disposed afterwards.
            var agent = service.Agent;
            Assert.NotNull(agent);
            var manager = ForceCreateSubAgentManager(agent!);

            CodingAgent? hookAgent = null;
            service.OnAgentDisposing = a =>
            {
                hookAgent = a;
                throw new InvalidOperationException("hook boom");
            };

            // The hook exception should propagate (non-failure path on SwitchModelAsync
            // calls DisposeClientsAndClearStateAsync directly, which rethrows).
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.SwitchModelAsync("model-b", ReasoningEffort.Medium, TestContext.Current.CancellationToken));
            Assert.Equal("hook boom", ex.Message);

            // The hook saw the old agent…
            Assert.Same(agent, hookAgent);

            // …and, crucially, the agent was ACTUALLY disposed despite the hook throwing:
            // its manager is disposed and refuses to start new sub-agents.
            Assert.True(GetSubAgentManagerIsDisposed(manager));
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => manager.StartAsync(
                    new SubAgentRequest
                    {
                        Task = "test",
                        Model = "model-a",
                        Timeout = TimeSpan.FromSeconds(30),
                    },
                    TestContext.Current.CancellationToken));

            // Client was still disposed even though the hook threw.
            Assert.True(clientDisposed);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── Exception precedence: operation-failure cleanup logs, not rethrows ──

    [Fact]
    public async Task ConnectAsync_CompactionCreationFails_CleanupDisposeFailureLogged_OriginalExceptionPropagates()
    {
        var stateDir = CreateTempDir();
        try
        {
            // Main client is created successfully, but its Dispose throws "cleanup boom".
            var mainClient = new Mock<IChatClient>();
            mainClient.Setup(c => c.Dispose())
                .Throws(new InvalidOperationException("cleanup boom"));

            // Factory returns mainClient for the main model, throws "creation boom" for compaction model.
            var service = CreateService(
                stateDir,
                chatClientFactory: m => m == "compaction-model"
                    ? throw new InvalidOperationException("creation boom")
                    : mainClient.Object,
                compactionModel: "compaction-model");

            // ConnectAsync: creates main client (OK), then tries compaction client → throws "creation boom".
            // Catch block calls SafeDisposeClientsAndClearStateAsync which disposes mainClient → throws "cleanup boom".
            // SafeDispose LOGS "cleanup boom" and swallows it. Original "creation boom" must propagate.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ConnectAsync(TestContext.Current.CancellationToken));
            Assert.Equal("creation boom", ex.Message);

            // State should be cleared despite cleanup failure.
            Assert.False(service.IsConnected);
            Assert.Null(GetField<IChatClient>(service, "_chatClient"));
            Assert.Null(GetField<IChatClient>(service, "_compactionChatClient"));
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── Lazy-manager setup: hook captures non-null manager, then ObjectDisposedException ──

    [Fact]
    public async Task DisposeAsync_AfterSubAgentManagerCreated_ManagerIsDisposed()
    {
        var stateDir = CreateTempDir();
        try
        {
            var hiveConfig = new HiveConfigFile
            {
                Models = new ModelsConfig
                {
                    AvailableModels = [new ModelEntry { Name = "gpt-4" }]
                }
            };

            var repoManager = new Mock<IBrainRepoManager>();
            repoManager.SetupGet(r => r.WorkDirectory).Returns(stateDir);

            var mockClient = new Mock<IChatClient>();
            var service = CreateService(
                stateDir,
                chatClientFactory: _ => mockClient.Object,
                hiveConfig: hiveConfig,
                repoManager: repoManager.Object,
                subAgentsEnabled: true);

            await service.ConnectAsync(TestContext.Current.CancellationToken);
            var agent = service.Agent;
            Assert.NotNull(agent);

            // Lazily create the SubAgentManager (normally null until first sub-agent use).
            // GetOrCreateSubAgentManager is private on CodingAgent — use reflection.
            var getManagerMethod = agent!.GetType().GetMethod("GetOrCreateSubAgentManager",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("GetOrCreateSubAgentManager not found");
            var manager = (SubAgentManager)getManagerMethod.Invoke(agent, null)!;
            Assert.NotNull(manager);

            // IsDisposed is internal on SubAgentManager — use reflection.
            var isDisposedProp = typeof(SubAgentManager).GetProperty("IsDisposed",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("IsDisposed property not found");
            Assert.False((bool)isDisposedProp.GetValue(manager)!);

            // Dispose the service — this disposes the agent, which disposes the manager.
            await service.DisposeAsync();

            // The manager should now be disposed.
            Assert.True((bool)isDisposedProp.GetValue(manager)!);

            // Starting a new sub-agent on the disposed manager must throw ObjectDisposedException.
            var request = new SubAgentRequest
            {
                Task = "test",
                Model = "gpt-4",
                Timeout = TimeSpan.FromSeconds(30),
            };
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => manager.StartAsync(request, TestContext.Current.CancellationToken));
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── Sub-agent client ownership: manager-owned, never disposed by the Composer ──

    [Fact]
    public async Task DisposeAsync_SubAgentClientIsOwnedByManager_NotDisposedByComposer()
    {
        var stateDir = CreateTempDir();
        try
        {
            var hiveConfig = new HiveConfigFile
            {
                Models = new ModelsConfig
                {
                    AvailableModels = [new ModelEntry { Name = "gpt-4" }]
                }
            };

            var repoManager = new Mock<IBrainRepoManager>();
            repoManager.SetupGet(r => r.WorkDirectory).Returns(stateDir);

            var mainClientMock = new Mock<IChatClient>();
            var subAgentClientMock = new Mock<IChatClient>();
            var callCount = 0;

            // First factory call is the Composer's own main client (ConnectAsync); every later
            // call comes from SubAgentOptions.ClientFactory and is owned by SubAgentManager.
            var service = CreateService(
                stateDir,
                chatClientFactory: _ => ++callCount == 1 ? mainClientMock.Object : subAgentClientMock.Object,
                hiveConfig: hiveConfig,
                repoManager: repoManager.Object,
                subAgentsEnabled: true);

            await service.ConnectAsync(TestContext.Current.CancellationToken);
            var agent = service.Agent;
            Assert.NotNull(agent);

            // Drive the real manager-owned path: the manager calls ClientFactory itself and
            // takes ownership of the returned client. No orphaned client is created here.
            var manager = ForceCreateSubAgentManager(agent!);
            var info = await manager.StartAsync(
                new SubAgentRequest
                {
                    Task = "summarise the repository layout",
                    Model = "gpt-4",
                    Timeout = TimeSpan.FromSeconds(30),
                },
                TestContext.Current.CancellationToken);
            Assert.NotNull(info);

            // The manager created its client through our factory.
            Assert.True(callCount >= 2, $"ClientFactory was not invoked by SubAgentManager (callCount={callCount}).");

            // The manager-owned client must never be adopted as Composer connection state.
            Assert.NotSame(subAgentClientMock.Object, GetField<IChatClient>(service, "_chatClient"));
            Assert.Null(GetField<IChatClient>(service, "_compactionChatClient"));

            // Let the sub-agent run settle so the manager reaches its own cleanup.
            await manager.AwaitAsync(null, TestContext.Current.CancellationToken);

            await service.DisposeAsync();

            // The Composer disposes exactly its own client…
            mainClientMock.Verify(c => c.Dispose(), Times.Once);

            // …and the manager-owned sub-agent client is disposed exactly once — by the
            // SubAgentManager (via the agent disposal chain), never a second time by the
            // Composer. Zero would be a leak; two would be a double-disposal.
            subAgentClientMock.Verify(c => c.Dispose(), Times.Once);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── Construction-time snapshot: BuildSubAgentOptions uses snapshot, not mutable _hiveConfig ──

    [Fact]
    public async Task BuildSubAgentOptions_UsesConstructionTimeSnapshot_NotMutableHiveConfig()
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
                        new ModelEntry { Name = "model-a" },
                        new ModelEntry { Name = "model-b" },
                    ]
                }
            };

            var repoManager = new Mock<IBrainRepoManager>();
            repoManager.SetupGet(r => r.WorkDirectory).Returns(stateDir);

            var mockClient = new Mock<IChatClient>();

            var service = CreateService(
                stateDir,
                chatClientFactory: _ => mockClient.Object,
                hiveConfig: hiveConfig,
                repoManager: repoManager.Object,
                subAgentsEnabled: true);

            // Connect — creates the agent with SubAgents from the construction-time snapshot.
            await service.ConnectAsync(TestContext.Current.CancellationToken);

            // Verify SubAgents is non-null with 2 entries from the snapshot.
            var subAgentsBefore = service.AgentOptions.SubAgents;
            Assert.NotNull(subAgentsBefore);
            Assert.Equal(2, subAgentsBefore!.AvailableModels.Count);
            Assert.Equal("model-a", subAgentsBefore.AvailableModels[0].Id);
            Assert.Equal("model-b", subAgentsBefore.AvailableModels[1].Id);

            // MUTATE the live hiveConfig — clear all models.
            hiveConfig.Models!.AvailableModels.Clear();

            // Recreate the agent — BuildSubAgentOptions should use the snapshot, not the
            // now-empty mutable config. If it reads from _hiveConfig, SubAgents would be null.
            await service.RecreateAgentAsync();

            // SubAgents must STILL be non-null with the original 2 entries.
            var subAgentsAfter = service.AgentOptions.SubAgents;
            Assert.NotNull(subAgentsAfter);
            Assert.Equal(2, subAgentsAfter!.AvailableModels.Count);
            Assert.Equal("model-a", subAgentsAfter.AvailableModels[0].Id);
            Assert.Equal("model-b", subAgentsAfter.AvailableModels[1].Id);

            await service.DisposeAsync();
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── AdditionalImagesRoot wiring ───────────────────────────────────────

    [Fact]
    public async Task BuildSubAgentOptions_WithAttachmentServiceRoot_SetsAdditionalImagesRootAndKeepsWorkDirectory()
    {
        var stateDir = CreateTempDir();
        var attachmentRoot = Path.Combine(stateDir, "composer-attachments");
        Directory.CreateDirectory(attachmentRoot);

        try
        {
            var repoManager = new Mock<IBrainRepoManager>();
            repoManager.SetupGet(r => r.WorkDirectory).Returns(stateDir);

            var mockClient = new Mock<IChatClient>();

            var service = CreateService(
                stateDir,
                chatClientFactory: _ => mockClient.Object,
                repoManager: repoManager.Object,
                subAgentsEnabled: true,
                subAgentModels:
                [
                    new ModelEntry { Name = "vision-model", SupportsVision = true }
                ],
                additionalImagesRoot: attachmentRoot);

            await service.ConnectAsync(TestContext.Current.CancellationToken);

            var subAgents = service.AgentOptions.SubAgents;
            Assert.NotNull(subAgents);
            Assert.Equal(attachmentRoot, subAgents!.AdditionalImagesRoot);
            Assert.Equal(stateDir, service.AgentOptions.WorkDirectory);

            await service.DisposeAsync();
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task BuildSubAgentOptions_WhenDisabled_IgnoresAdditionalImagesRootAndReturnsNull()
    {
        var stateDir = CreateTempDir();
        var attachmentRoot = Path.Combine(stateDir, "composer-attachments");
        Directory.CreateDirectory(attachmentRoot);

        try
        {
            var repoManager = new Mock<IBrainRepoManager>();
            repoManager.SetupGet(r => r.WorkDirectory).Returns(stateDir);

            var mockClient = new Mock<IChatClient>();

            var service = CreateService(
                stateDir,
                chatClientFactory: _ => mockClient.Object,
                repoManager: repoManager.Object,
                subAgentsEnabled: false,
                subAgentModels:
                [
                    new ModelEntry { Name = "vision-model", SupportsVision = true }
                ],
                additionalImagesRoot: attachmentRoot);

            await service.ConnectAsync(TestContext.Current.CancellationToken);

            Assert.Null(service.AgentOptions.SubAgents);

            await service.DisposeAsync();
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task BuildSubAgentOptions_WhenRepoManagerNull_IgnoresAdditionalImagesRootAndReturnsNull()
    {
        var stateDir = CreateTempDir();
        var attachmentRoot = Path.Combine(stateDir, "composer-attachments");
        Directory.CreateDirectory(attachmentRoot);

        try
        {
            var mockClient = new Mock<IChatClient>();

            var service = CreateService(
                stateDir,
                chatClientFactory: _ => mockClient.Object,
                repoManager: null,
                subAgentsEnabled: true,
                subAgentModels:
                [
                    new ModelEntry { Name = "vision-model", SupportsVision = true }
                ],
                additionalImagesRoot: attachmentRoot);

            await service.ConnectAsync(TestContext.Current.CancellationToken);

            Assert.Null(service.AgentOptions.SubAgents);

            await service.DisposeAsync();
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    // ── Composer reset and attachment service integration ─────────────────

    [Fact]
    public async Task Composer_ResetSessionAsync_ClearsAttachmentsAfterAgentReset_AfterDisposalAndAllFilesRemoved()
    {
        var stateDir = CreateTempDir();
        Composer? composer = null;
        CopilotHive.Persistence.CopilotHiveDbContext? dbContext = null;
        try
        {
            var hiveConfig = new HiveConfigFile
            {
                Models = new ModelsConfig
                {
                    AvailableModels =
                    [
                        new ModelEntry { Name = "gpt-4" },
                    ]
                }
            };

            var repoManager = new Mock<IBrainRepoManager>();
            repoManager.SetupGet(r => r.WorkDirectory).Returns(stateDir);

            dbContext = CopilotHive.Persistence.CopilotHiveDbContext.CreateInMemory();
            var store = new CopilotHive.Goals.GoalStore(
                dbContext,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<CopilotHive.Goals.GoalStore>.Instance);

            var attachmentService = new CopilotHive.Services.ComposerAttachmentService(
                stateDir,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<CopilotHive.Services.ComposerAttachmentService>.Instance);

            // Seed two attachment files so a single ClearAllAsync removes both; partial deletion
            // would leave at least one behind. Exactly-once is proven by path exclusion: the
            // overflow-recovery test (RunStreamingAsync_ContextOverflow_DoesNotClearComposerAttachments)
            // proves the only other reset path does NOT clear attachments.
            var first = await attachmentService.SaveAsync(
                "diagram.png",
                new System.IO.MemoryStream(new byte[] { 0x01, 0x02, 0x03 }),
                TestContext.Current.CancellationToken);
            var second = await attachmentService.SaveAsync(
                "report.pdf",
                new System.IO.MemoryStream(new byte[] { 0x04, 0x05, 0x06 }),
                TestContext.Current.CancellationToken);
            Assert.True(first.Success);
            Assert.True(second.Success);

            var mockClient = new Mock<IChatClient>();
            composer = new Composer(
                "test-model",
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Composer>.Instance,
                store,
                repoManager: repoManager.Object,
                stateDir: stateDir,
                hiveConfig: hiveConfig,
                chatClientFactory: _ => mockClient.Object,
                attachmentService: attachmentService);

            await composer.ConnectAsync(TestContext.Current.CancellationToken);
            Assert.True(composer.IsConnected);

            var agentService = GetField<ComposerAgentService>(composer, "_agentService")!;
            var seededFilesBeforeDisposal = Array.Empty<string>();
            agentService.OnAgentDisposing = _ =>
            {
                // Capture the directory contents at the moment agent disposal begins.
                // ClearAllAsync must NOT have run yet, so both seeded files must still be present.
                seededFilesBeforeDisposal = Directory.GetFiles(attachmentService.AttachmentsRootPath);
            };

            await composer.ResetSessionAsync(TestContext.Current.CancellationToken);

            Assert.Equal(2, seededFilesBeforeDisposal.Length);
            Assert.Contains(Path.Combine(attachmentService.AttachmentsRootPath, first.Attachment!.SavedRelativePath), seededFilesBeforeDisposal);
            Assert.Contains(Path.Combine(attachmentService.AttachmentsRootPath, second.Attachment!.SavedRelativePath), seededFilesBeforeDisposal);

            var remainingFiles = Directory.GetFiles(attachmentService.AttachmentsRootPath);
            Assert.Empty(remainingFiles);
        }
        finally
        {
            if (composer is not null)
            {
                try { await composer.DisposeAsync(); }
                catch (Exception) { /* best effort */ }
            }
            dbContext?.Dispose();
            TryDeleteDir(stateDir);
        }
    }

    // ── Sub-agent model descriptions ──

    [Fact]
    public async Task BuildSubAgentOptions_UsesConfiguredDescription_WhenPresent()
    {
        var stateDir = CreateTempDir();
        ComposerAgentService? service = null;
        try
        {
            var repoManager = new Mock<IBrainRepoManager>();
            repoManager.SetupGet(r => r.WorkDirectory).Returns(stateDir);
            var mockClient = new Mock<IChatClient>();

            service = CreateService(
                stateDir,
                chatClientFactory: _ => mockClient.Object,
                repoManager: repoManager.Object,
                subAgentsEnabled: true,
                subAgentModels:
                [
                    new ModelEntry
                    {
                        Name = "model-a",
                        ContextWindow = 128_000,
                        Description = "Best for wide code search"
                    }
                ]);

            await service.ConnectAsync(TestContext.Current.CancellationToken);

            var subAgents = service.AgentOptions.SubAgents;
            Assert.NotNull(subAgents);
            var info = Assert.Single(subAgents!.AvailableModels);
            Assert.Equal("model-a", info.Id);
            // Must be the configured description, not the auto-generated fallback.
            Assert.Equal("Best for wide code search", info.Description);
        }
        finally
        {
            if (service is not null)
                await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BuildSubAgentOptions_FallsBackToAutoDescription_WhenBlank(string? description)
    {
        var stateDir = CreateTempDir();
        ComposerAgentService? service = null;
        try
        {
            var repoManager = new Mock<IBrainRepoManager>();
            repoManager.SetupGet(r => r.WorkDirectory).Returns(stateDir);
            var mockClient = new Mock<IChatClient>();

            service = CreateService(
                stateDir,
                chatClientFactory: _ => mockClient.Object,
                repoManager: repoManager.Object,
                subAgentsEnabled: true,
                subAgentModels:
                [
                    new ModelEntry { Name = "model-a", ContextWindow = 128_000, Description = description },
                    new ModelEntry { Name = "model-b", ContextWindow = null, Description = description },
                ]);

            await service.ConnectAsync(TestContext.Current.CancellationToken);

            var subAgents = service.AgentOptions.SubAgents;
            Assert.NotNull(subAgents);
            Assert.Equal("Configured model, 128K context window", subAgents!.AvailableModels[0].Description);
            Assert.Equal("Configured model", subAgents.AvailableModels[1].Description);
        }
        finally
        {
            if (service is not null)
                await service.DisposeAsync();
            TryDeleteDir(stateDir);
        }
    }

    // ── Utility ──

    /// <summary>
    /// Forces creation of the lazily-created <see cref="SubAgentManager"/> on a
    /// <see cref="CodingAgent"/>. <c>GetOrCreateSubAgentManager</c> is private — use reflection.
    /// </summary>
    private static SubAgentManager ForceCreateSubAgentManager(CodingAgent agent)
    {
        var method = agent.GetType().GetMethod("GetOrCreateSubAgentManager",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("GetOrCreateSubAgentManager not found on CodingAgent");
        return (SubAgentManager)method.Invoke(agent, null)!;
    }

    /// <summary>Reads the internal <c>IsDisposed</c> flag from a <see cref="SubAgentManager"/>.</summary>
    private static bool GetSubAgentManagerIsDisposed(SubAgentManager manager)
    {
        var prop = typeof(SubAgentManager).GetProperty("IsDisposed",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("IsDisposed property not found on SubAgentManager");
        return (bool)prop.GetValue(manager)!;
    }

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