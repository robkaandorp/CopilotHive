using System.Reflection;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.AI;
using Moq;
using System.Net;
using System.Text.Json;
using System.Net.Http.Json;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Tests for Composer's multi-model runtime switching.
/// </summary>
public sealed class ComposerMultiModelTests : IDisposable
{
    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _store;
    private readonly Composer _composer;

    public ComposerMultiModelTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);

        _composer = new Composer(
            "claude-sonnet-4",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            availableModels: ["claude-sonnet-4", "claude-opus"],
            chatClientFactory: _ => new Mock<IChatClient>().Object);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    // ── Disconnected-shell construction (no valid default) ──

    /// <summary>
    /// A chat-client factory that COUNTS its invocations so a test can prove no LLM client was
    /// ever created — not merely that the final field reads null. A regression that creates a
    /// client and discards it would still be caught.
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

        /// <summary>The delegate handed to the Composer under test.</summary>
        public Func<string, IChatClient> Delegate => modelId =>
        {
            Interlocked.Increment(ref _invocations);
            lock (_lock) { _requestedModels.Add(modelId); }
            return new Mock<IChatClient>().Object;
        };
    }

    [Fact]
    public async Task Constructor_NoModel_ConstructsDisconnectedShell_ClientFactoryNeverInvoked()
    {
        var factory = new CountingChatClientFactory();
        var composer = new Composer(
            null,
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            chatClientFactory: factory.Delegate);

        await using (composer)
        {
            Assert.False(composer.IsConnected);
            Assert.Null(composer.StartupDefaultModel);
            Assert.Null(AgentServiceOf(composer).Model);
            Assert.Null(AgentServiceOf(composer).ChatClient);
            Assert.Null(AgentServiceOf(composer).Agent);

            // Authoritative no-client proof: the factory was NEVER invoked.
            Assert.Equal(0, factory.Invocations);
            Assert.Empty(factory.RequestedModels);
        }
    }

    [Fact]
    public async Task Constructor_WhitespaceModel_NormalizesToNull_ClientFactoryNeverInvoked()
    {
        var factory = new CountingChatClientFactory();
        var composer = new Composer(
            "   ",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            chatClientFactory: factory.Delegate);

        await using (composer)
        {
            Assert.False(composer.IsConnected);
            Assert.Null(composer.StartupDefaultModel);
            Assert.Null(AgentServiceOf(composer).Model);

            Assert.Equal(0, factory.Invocations);
            Assert.Empty(factory.RequestedModels);
        }
    }

    /// <summary>
    /// Control test: the counting factory really does observe client creation, so the
    /// zero-invocation assertions above are not vacuous. With a configured model,
    /// <c>ConnectAsync</c> invokes the factory exactly once for that model.
    /// </summary>
    [Fact]
    public async Task ConnectAsync_WithModel_ClientFactoryInvokedForThatModel()
    {
        var factory = new CountingChatClientFactory();
        var composer = new Composer(
            "claude-sonnet-4",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            availableModels: ["claude-sonnet-4"],
            chatClientFactory: factory.Delegate);

        await using (composer)
        {
            // Construction alone creates nothing.
            Assert.Equal(0, factory.Invocations);

            await composer.ConnectAsync(TestContext.Current.CancellationToken);

            Assert.True(composer.IsConnected);
            Assert.Equal(1, factory.Invocations);
            Assert.Equal(["claude-sonnet-4"], factory.RequestedModels);
        }
    }

    [Fact]
    public async Task Constructor_NoModel_StartupCatalogEmpty_NoNullEntry()
    {
        var composer = new Composer(
            null,
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        await using (composer)
        {
            // No model → startup catalog is EMPTY (no [null] entry, no fabricated fallback).
            Assert.Empty(AgentServiceOf(composer).AvailableModels);
        }
    }

    [Fact]
    public async Task Constructor_NoModel_WithStartupCatalog_KeepsCatalog()
    {
        var composer = new Composer(
            null,
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            availableModels: ["model-a", "model-b"],
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        await using (composer)
        {
            Assert.Equal(2, AgentServiceOf(composer).AvailableModels.Count);
            Assert.Equal("model-a", AgentServiceOf(composer).AvailableModels[0]);
            Assert.Equal("model-b", AgentServiceOf(composer).AvailableModels[1]);
        }
    }

    [Fact]
    public async Task ConnectAsync_NoModel_ThrowsNoModelConfigured_ClientFactoryNeverInvoked()
    {
        var factory = new CountingChatClientFactory();
        var composer = new Composer(
            null,
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            chatClientFactory: factory.Delegate);

        await using (composer)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => composer.ConnectAsync(TestContext.Current.CancellationToken));
            Assert.Equal("no model configured", ex.Message);

            Assert.False(composer.IsConnected);
            Assert.Null(AgentServiceOf(composer).ChatClient);
            Assert.Null(AgentServiceOf(composer).Agent);

            // No LLM client was created anywhere on the connect path.
            Assert.Equal(0, factory.Invocations);
            Assert.Empty(factory.RequestedModels);
        }
    }

    [Fact]
    public async Task ConnectAsync_WhitespaceModel_ThrowsNoModelConfigured_ClientFactoryNeverInvoked()
    {
        var factory = new CountingChatClientFactory();
        var composer = new Composer(
            " \t ",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            chatClientFactory: factory.Delegate);

        await using (composer)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => composer.ConnectAsync(TestContext.Current.CancellationToken));
            Assert.Equal("no model configured", ex.Message);
            Assert.False(composer.IsConnected);

            Assert.Equal(0, factory.Invocations);
            Assert.Empty(factory.RequestedModels);
        }
    }

    [Fact]
    public async Task SwitchModelAsync_FromDisconnectedShell_Connects()
    {
        var composer = new Composer(
            null,
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            availableModels: ["model-a", "model-b"],
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        await using (composer)
        {
            Assert.False(composer.IsConnected);

            // Interim connect-on-select: valid model in catalog → client+agent created,
            // CONNECTS (no session disk-load this slice).
            await composer.SwitchModelAsync("model-b", ReasoningEffort.Medium, TestContext.Current.CancellationToken);

            Assert.True(composer.IsConnected);
            var stats = composer.GetStats();
            Assert.NotNull(stats);
            Assert.Equal("model-b", stats!.Model);
        }
    }

    [Fact]
    public async Task GetStats_NoModel_ReturnsNull()
    {
        var composer = new Composer(
            null,
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        await using (composer)
        {
            // Disconnected shell: no agent → GetStats returns null (never a null Model in BrainStats).
            Assert.Null(composer.GetStats());
        }
    }

    [Fact]
    public async Task RefreshComposerRegistry_NoModel_DoesNotWriteNullModelEntry()
    {
        var registry = new LlmSessionRegistry();
        var composer = new Composer(
            null,
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            sessionRegistry: registry,
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        await using (composer)
        {
            // Trigger the registry refresh path (e.g. via ResetSessionAsync which calls
            // RefreshComposerRegistry) — with no model, NO entry may be written.
            // ResetSessionAsync requires connection, so invoke the private method via
            // reflection to exercise the guard directly.
            var method = typeof(Composer).GetMethod("RefreshComposerRegistry",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("RefreshComposerRegistry not found");
            method.Invoke(composer, ["idle", null]);

            Assert.Empty(registry.GetAll());
        }
    }

    [Fact]
    public async Task StartupDefaultModel_WithValidModel_IsResolvedModel()
    {
        var composer = new Composer(
            "claude-sonnet-4",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            availableModels: ["claude-sonnet-4", "claude-opus"],
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        await using (composer)
        {
            Assert.Equal("claude-sonnet-4", composer.StartupDefaultModel);
        }
    }

    [Fact]
    public async Task StartupDefaultModel_WithWhitespaceModel_IsNull()
    {
        var composer = new Composer(
            "   ",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        await using (composer)
        {
            Assert.Null(composer.StartupDefaultModel);
        }
    }

    // ── AvailableModels Property ──

    [Fact]
    public void AvailableModels_ReturnsConfiguredModels()
    {
        var models = _composer.AvailableModels;

        Assert.Equal(2, models.Count);
        Assert.Equal("claude-sonnet-4", models[0]);
        Assert.Equal("claude-opus", models[1]);
    }

    [Fact]
    public void AvailableModels_WithNoAvailableModels_ReturnsSingleDefault()
    {
        var composer = new Composer(
            "default-model",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        var models = composer.AvailableModels;

        Assert.Single(models);
        Assert.Equal("default-model", models[0]);
    }

    // ── SwitchModelAsync ──

    [Fact]
    public async Task SwitchModelAsync_ToValidModel_Succeeds()
    {
        await _composer.SwitchModelAsync("claude-opus", ReasoningEffort.Medium, TestContext.Current.CancellationToken);

        // No exception means success - verify by checking stats
        var stats = _composer.GetStats();
        Assert.Equal("claude-opus", stats?.Model);
    }

    [Fact]
    public async Task SwitchModelAsync_ToInvalidModel_ThrowsArgumentException()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _composer.SwitchModelAsync("unknown-model", ReasoningEffort.Medium, TestContext.Current.CancellationToken));

        Assert.Contains("unknown-model", exception.Message);
        Assert.Contains("Available models:", exception.Message);
    }

    [Fact]
    public async Task SwitchModelAsync_IsCaseInsensitive()
    {
        // Should not throw - model matching is case-insensitive
        await _composer.SwitchModelAsync("CLAUDE-OPUS", ReasoningEffort.Medium, TestContext.Current.CancellationToken);

        var stats = _composer.GetStats();
        Assert.Equal("CLAUDE-OPUS", stats?.Model);
    }

    [Fact]
    public async Task SwitchModelAsync_PreservesSessionHistory()
    {
        // Create initial session with some content
        await _composer.ConnectAsync(TestContext.Current.CancellationToken);
        
        // Switch model
        await _composer.SwitchModelAsync("claude-opus", ReasoningEffort.Medium, TestContext.Current.CancellationToken);

        // Session should still exist (verified via stats)
        var stats = _composer.GetStats();
        Assert.NotNull(stats);
    }

    // ── Configured reasoning effort wiring ──

    private static ComposerAgentService AgentServiceOf(Composer composer) =>
        (ComposerAgentService)typeof(Composer)
            .GetField("_agentService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(composer)!;

    [Fact]
    public async Task ConfiguredReasoningEffort_IsPassedToAgentService_AndOverridesSuffix()
    {
        var composer = new Composer(
            "claude-sonnet-4:low",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            availableModels: ["claude-sonnet-4:low", "claude-opus"],
            chatClientFactory: _ => new Mock<IChatClient>().Object,
            reasoningEffort: ReasoningEffort.High);

        await using (composer)
        {
            var agentService = AgentServiceOf(composer);
            Assert.Equal(ReasoningEffort.High, agentService.ReasoningEffort);
        }
    }

    /// <summary>
    /// Without a configured reasoning effort the value stays unset — a ':low' colon segment
    /// in the model name is never parsed as a reasoning level.
    /// </summary>
    [Fact]
    public async Task NoConfiguredReasoningEffort_AgentServiceLeavesReasoningUnset()
    {
        var composer = new Composer(
            "claude-sonnet-4:low",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            availableModels: ["claude-sonnet-4:low", "claude-opus"],
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        await using (composer)
        {
            var agentService = AgentServiceOf(composer);
            Assert.Null(agentService.ReasoningEffort);
        }
    }

    [Fact]
    public async Task SwitchModelAsync_AppliesSuppliedReasoningEffort_IgnoringColonSegment()
    {
        var composer = new Composer(
            "claude-sonnet-4",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            availableModels: ["claude-sonnet-4", "claude-opus:low"],
            chatClientFactory: _ => new Mock<IChatClient>().Object,
            reasoningEffort: ReasoningEffort.High);

        await using (composer)
        {
            await composer.ConnectAsync(TestContext.Current.CancellationToken);
            await composer.SwitchModelAsync("claude-opus:low", ReasoningEffort.Medium, TestContext.Current.CancellationToken);

            // The ':low' colon segment of the new model name is irrelevant — only the
            // explicitly supplied effort is applied.
            Assert.Equal(ReasoningEffort.Medium, AgentServiceOf(composer).ReasoningEffort);
            Assert.Equal(ReasoningEffort.Medium, composer.ReasoningEffort);
        }
    }

    [Fact]
    public async Task SwitchModelAsync_FromUnsetReasoning_AdoptsSuppliedEffort()
    {
        var composer = new Composer(
            "claude-sonnet-4:high",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            availableModels: ["claude-sonnet-4:high", "claude-opus"],
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        await using (composer)
        {
            await composer.ConnectAsync(TestContext.Current.CancellationToken);
            // No configured value → unset, despite the ':high' segment in the model name.
            Assert.Null(AgentServiceOf(composer).ReasoningEffort);
            Assert.Null(composer.ReasoningEffort);

            await composer.SwitchModelAsync("claude-opus", ReasoningEffort.None, TestContext.Current.CancellationToken);

            Assert.Equal(ReasoningEffort.None, AgentServiceOf(composer).ReasoningEffort);
            Assert.Equal(ReasoningEffort.None, composer.ReasoningEffort);
        }
    }

    [Fact]
    public async Task AvailableModels_ReflectsLiveHiveConfigMutation()
    {
        // Arrange: Composer started without "gpt-4" in its startup list,
        // but with a HiveConfigFile whose global list also lacks it initially.
        var liveConfig = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "claude-sonnet-4" },
                    new ModelEntry { Name = "claude-opus" }
                ]
            }
        };

        var composer = new Composer(
            "claude-sonnet-4",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            availableModels: ["claude-sonnet-4", "claude-opus"],
            hiveConfig: liveConfig,
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        // Act 1: "gpt-4" is not in the global list → should throw
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => composer.SwitchModelAsync("gpt-4", ReasoningEffort.Medium, TestContext.Current.CancellationToken));
        Assert.Contains("gpt-4", ex.Message);

        // Act 2: mutate the global config to add "gpt-4"
        liveConfig.Models!.AvailableModels!.Add(new ModelEntry { Name = "gpt-4" });

        // Act 3: "gpt-4" is now in the global list → should succeed
        await composer.SwitchModelAsync("gpt-4", ReasoningEffort.Medium, TestContext.Current.CancellationToken);
        var stats = composer.GetStats();
        Assert.Equal("gpt-4", stats?.Model);
    }

    // ── Composite model context window regression tests ──

    /// <summary>
    /// A model name containing a colon segment (e.g. "gpt-4:medium") is matched verbatim
    /// against <c>ModelEntry.Name</c> — nothing is stripped before the context-window lookup.
    /// </summary>
    [Fact]
    public async Task SwitchModelAsync_ColonSegmentModel_UpdatesContextWindowFromConfig()
    {
        // Arrange: global config with a ModelEntry that has an explicit ContextWindow
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "claude-sonnet-4" },
                    new ModelEntry { Name = "gpt-4:medium", ContextWindow = 32768 }
                ]
            }
        };

        // Composer starts with claude-sonnet-4 and a max context of 64000 (different from 32768)
        var composer = new Composer(
            "claude-sonnet-4",
            NullLogger<Composer>.Instance,
            _store,
            maxContextTokens: 64000,
            stateDir: Path.GetTempPath(),
            availableModels: ["claude-sonnet-4", "gpt-4:medium"],
            hiveConfig: config,
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        // Pre-condition: verify initial max context tokens
        Assert.Equal(64000, composer.GetStats()?.MaxContextTokens ?? 64000);

        // Act: switch to the model whose name contains a colon segment
        await composer.SwitchModelAsync("gpt-4:medium", ReasoningEffort.Medium, TestContext.Current.CancellationToken);

        // Assert: the context window is 32768 because the lookup matched "gpt-4:medium" verbatim.
        var stats = composer.GetStats();
        Assert.NotNull(stats);
        Assert.Equal(32768, stats!.MaxContextTokens);
        Assert.Equal("gpt-4:medium", stats.Model);
    }

    /// <summary>
    /// Regression test: when switching to a model whose ModelEntry has no ContextWindow set,
    /// the existing max context tokens must be preserved (the lookup returns null and the
    /// code skips the update).
    /// </summary>
    [Fact]
    public async Task SwitchModelAsync_ColonSegmentModel_NoContextWindow_PreservesExistingMaxTokens()
    {
        // Arrange: global config with a ModelEntry that has NO ContextWindow set
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "claude-sonnet-4" },
                    new ModelEntry { Name = "gpt-4:medium" }
                ]
            }
        };

        // Composer starts with claude-sonnet-4 and a max context of 64000
        var composer = new Composer(
            "claude-sonnet-4",
            NullLogger<Composer>.Instance,
            _store,
            maxContextTokens: 64000,
            stateDir: Path.GetTempPath(),
            availableModels: ["claude-sonnet-4", "gpt-4:medium"],
            hiveConfig: config,
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        // Act: switch to the composite model string
        await composer.SwitchModelAsync("gpt-4:medium", ReasoningEffort.Medium, TestContext.Current.CancellationToken);

        // Assert: the max context tokens should be unchanged at 64000 because
        // the ModelEntry has no ContextWindow (lookup returns null)
        var stats = composer.GetStats();
        Assert.NotNull(stats);
        Assert.Equal(64000, stats!.MaxContextTokens);
        Assert.Equal("gpt-4:medium", stats.Model);
    }

    /// <summary>
    /// Regression test: when switching to a plain model name with a configured
    /// ContextWindow, the context window should be updated.
    /// </summary>
    [Fact]
    public async Task SwitchModelAsync_PlainModel_UpdatesContextWindowFromConfig()
    {
        // Arrange: global config with a ModelEntry that has an explicit ContextWindow
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "claude-sonnet-4" },
                    new ModelEntry { Name = "gpt-4", ContextWindow = 32768 }
                ]
            }
        };

        var composer = new Composer(
            "claude-sonnet-4",
            NullLogger<Composer>.Instance,
            _store,
            maxContextTokens: 64000,
            stateDir: Path.GetTempPath(),
            availableModels: ["claude-sonnet-4", "gpt-4"],
            hiveConfig: config,
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        // Act: switch to the plain model name (no suffix)
        await composer.SwitchModelAsync("gpt-4", ReasoningEffort.Medium, TestContext.Current.CancellationToken);

        // Assert: the context window should have been updated to 32768
        var stats = composer.GetStats();
        Assert.NotNull(stats);
        Assert.Equal(32768, stats!.MaxContextTokens);
        Assert.Equal("gpt-4", stats.Model);
    }

    /// <summary>
    /// Regression test: switching between models with different context windows correctly
    /// updates the value in both directions (shrinking and growing).
    /// </summary>
    [Fact]
    public async Task SwitchModelAsync_ColonSegmentModel_UpdatesContextWindowInBothDirections()
    {
        // Arrange: global config with two models having different context windows
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "claude-sonnet-4:high", ContextWindow = 100000 },
                    new ModelEntry { Name = "gpt-4:medium", ContextWindow = 32768 }
                ]
            }
        };

        var composer = new Composer(
            "claude-sonnet-4:high",
            NullLogger<Composer>.Instance,
            _store,
            maxContextTokens: 150000,
            stateDir: Path.GetTempPath(),
            availableModels: ["claude-sonnet-4:high", "gpt-4:medium"],
            hiveConfig: config,
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        // Act 1: switch to gpt-4:medium (smaller context window)
        await composer.SwitchModelAsync("gpt-4:medium", ReasoningEffort.Medium, TestContext.Current.CancellationToken);
        var stats1 = composer.GetStats();
        Assert.NotNull(stats1);
        Assert.Equal(32768, stats1!.MaxContextTokens);

        // Act 2: switch back to claude-sonnet-4:high (larger context window)
        await composer.SwitchModelAsync("claude-sonnet-4:high", ReasoningEffort.Medium, TestContext.Current.CancellationToken);
        var stats2 = composer.GetStats();
        Assert.NotNull(stats2);
        Assert.Equal(100000, stats2!.MaxContextTokens);
    }

    // ── Provider-prefixed composite model context window regression tests ──

    /// <summary>
    /// Regression test: a provider-prefixed model name with a colon segment
    /// (e.g. "copilot/claude-sonnet-4.6:high") is matched verbatim against
    /// <c>ModelEntry.Name</c> — neither the prefix nor the colon segment is stripped.
    /// </summary>
    [Fact]
    public async Task SwitchModelAsync_ProviderPrefixedColonSegment_UpdatesContextWindow()
    {
        // Arrange: global config with a ModelEntry whose Name includes the provider prefix
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "claude-sonnet-4" },
                    new ModelEntry { Name = "copilot/claude-sonnet-4.6:high", ContextWindow = 100000 }
                ]
            }
        };

        // Composer starts with maxContextTokens=64000 (different from 100000)
        var composer = new Composer(
            "claude-sonnet-4",
            NullLogger<Composer>.Instance,
            _store,
            maxContextTokens: 64000,
            stateDir: Path.GetTempPath(),
            availableModels: ["claude-sonnet-4", "copilot/claude-sonnet-4.6:high"],
            hiveConfig: config,
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        // Act: switch to the provider-prefixed model name
        await composer.SwitchModelAsync("copilot/claude-sonnet-4.6:high", ReasoningEffort.Medium, TestContext.Current.CancellationToken);

        // Assert: the context window is 100000 because the lookup matched the full
        // "copilot/claude-sonnet-4.6:high" name verbatim.
        var stats = composer.GetStats();
        Assert.NotNull(stats);
        Assert.Equal(100000, stats!.MaxContextTokens);
        Assert.Equal("copilot/claude-sonnet-4.6:high", stats.Model);
    }

    /// <summary>
    /// Regression test: an Ollama-style tagged model name (e.g. "ollama-cloud/gpt-oss:120b")
    /// is matched verbatim — the tag is part of the name and is never stripped.
    /// </summary>
    [Fact]
    public async Task SwitchModelAsync_OllamaTaggedModel_UpdatesContextWindow()
    {
        // Arrange: global config with an Ollama-style tagged model
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "claude-sonnet-4" },
                    new ModelEntry { Name = "ollama-cloud/gpt-oss:120b", ContextWindow = 200000 }
                ]
            }
        };

        // Composer starts with maxContextTokens=64000 (different from 200000)
        var composer = new Composer(
            "claude-sonnet-4",
            NullLogger<Composer>.Instance,
            _store,
            maxContextTokens: 64000,
            stateDir: Path.GetTempPath(),
            availableModels: ["claude-sonnet-4", "ollama-cloud/gpt-oss:120b"],
            hiveConfig: config,
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        // Act: switch to the Ollama-style tagged model
        await composer.SwitchModelAsync("ollama-cloud/gpt-oss:120b", ReasoningEffort.Medium, TestContext.Current.CancellationToken);

        // Assert: the context window is 200000 because the full tagged name matched verbatim.
        var stats = composer.GetStats();
        Assert.NotNull(stats);
        Assert.Equal(200000, stats!.MaxContextTokens);
        Assert.Equal("ollama-cloud/gpt-oss:120b", stats.Model);
    }

    /// <summary>
    /// Regression test: when switching to a provider-prefixed model whose ModelEntry has no
    /// ContextWindow set, the existing max context tokens must be preserved (the lookup
    /// returns null and the code skips the update).
    /// </summary>
    [Fact]
    public async Task SwitchModelAsync_ProviderPrefixedColonSegment_NoContextWindow_PreservesExisting()
    {
        // Arrange: global config with a ModelEntry that has NO ContextWindow set
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "claude-sonnet-4" },
                    new ModelEntry { Name = "copilot/claude-sonnet-4.6:high" }
                ]
            }
        };

        // Composer starts with maxContextTokens=64000
        var composer = new Composer(
            "claude-sonnet-4",
            NullLogger<Composer>.Instance,
            _store,
            maxContextTokens: 64000,
            stateDir: Path.GetTempPath(),
            availableModels: ["claude-sonnet-4", "copilot/claude-sonnet-4.6:high"],
            hiveConfig: config,
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        // Act: switch to the provider-prefixed model name
        await composer.SwitchModelAsync("copilot/claude-sonnet-4.6:high", ReasoningEffort.Medium, TestContext.Current.CancellationToken);

        // Assert: the max context tokens should be unchanged at 64000 because
        // the ModelEntry has no ContextWindow (lookup returns null)
        var stats = composer.GetStats();
        Assert.NotNull(stats);
        Assert.Equal(64000, stats!.MaxContextTokens);
        Assert.Equal("copilot/claude-sonnet-4.6:high", stats.Model);
    }
}

/// <summary>
/// Tests for ComposerHub REST API endpoints.
/// </summary>
public sealed class ComposerHubTests : IAsyncLifetime
{
    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _store;
    private readonly Composer _composer;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public ComposerHubTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);

        _composer = new Composer(
            "claude-sonnet-4",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            availableModels: ["claude-sonnet-4", "claude-opus"],
            chatClientFactory: _ => new Mock<IChatClient>().Object);
    }

    public async ValueTask InitializeAsync()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:0");
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<Composer>(_composer);
        // Wire the production global JSON options so enum payloads (reasoningEffort) are
        // serialized exactly as the real host does, rather than with framework defaults.
        Program.AddHiveJsonOptions(builder.Services);
        _app = builder.Build();
        _app.MapComposerEndpoints(
            _composer,
            new ComposerFacade(_composer, NullLogger<ComposerFacade>.Instance),
            config: null);
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app != null!)
            await _app.DisposeAsync();
        _dbContext.Dispose();
    }

    // ── GET /api/composer/current-model (frozen contract) ──

    [Fact]
    public async Task CurrentModel_NotConnected_ReturnsHttp200WithNullModel()
    {
        // The fixture's Composer is constructed with a model but never connected — a
        // disconnected shell. The frozen contract: HTTP 200 with {"model":null}, never a
        // fabricated catalog entry (no FirstOrDefault fallback).
        var response = await _client.GetAsync("/api/composer/current-model", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = JsonDocument.Parse(content);
        var model = json.RootElement.GetProperty("model");

        Assert.Equal(JsonValueKind.Null, model.ValueKind);
    }

    [Fact]
    public async Task CurrentModel_Connected_ReturnsActiveModel()
    {
        // Connect by switching to a valid model (Slice 1A2b-1 first-selection connect).
        var switchResponse = await _client.PostAsync(
            "/api/composer/models/switch?model=claude-opus&reasoning=medium",
            null, TestContext.Current.CancellationToken);
        switchResponse.EnsureSuccessStatusCode();

        var response = await _client.GetAsync("/api/composer/current-model", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = JsonDocument.Parse(content);
        var model = json.RootElement.GetProperty("model");

        Assert.Equal(JsonValueKind.String, model.ValueKind);
        Assert.Equal("claude-opus", model.GetString());
    }

    // ── GET /api/composer/models ──

    [Fact]
    public async Task GetModels_ReturnsAvailableModels()
    {
        var response = await _client.GetAsync("/api/composer/models", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = JsonDocument.Parse(content);
        var models = json.RootElement.GetProperty("models");

        Assert.Equal(2, models.GetArrayLength());

        var modelsList = models.EnumerateArray()
            .Select(m => m.GetString())
            .ToList();

        Assert.Equal("claude-sonnet-4", modelsList[0]);
        Assert.Equal("claude-opus", modelsList[1]);
    }

    [Fact]
    public async Task GetModels_ReturnsJsonContentType()
    {
        var response = await _client.GetAsync("/api/composer/models", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetModels_IncludesReasoningEffortField()
    {
        var response = await _client.GetAsync("/api/composer/models", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.True(json.RootElement.TryGetProperty("reasoningEffort", out var effort),
            "GET /api/composer/models must expose the current reasoning effort");
        // No configured effort on this fixture → null, which the client maps to None.
        Assert.Equal(JsonValueKind.Null, effort.ValueKind);
    }

    [Fact]
    public async Task GetModels_SerializesReasoningEffortAsSnakeCase()
    {
        // Switch to ExtraHigh first, then read it back through the GET projection.
        var switchResponse = await _client.PostAsync(
            "/api/composer/models/switch?model=claude-opus&reasoning=extra_high",
            null, TestContext.Current.CancellationToken);
        switchResponse.EnsureSuccessStatusCode();

        var response = await _client.GetAsync("/api/composer/models", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // The global converter renders the enum snake_case, never "ExtraHigh".
        Assert.Equal("extra_high", json.RootElement.GetProperty("reasoningEffort").GetString());
    }

    // ── POST /api/composer/models/switch ──

    [Fact]
    public async Task SwitchModel_ToValidModel_ReturnsOk()
    {
        var response = await _client.PostAsync("/api/composer/models/switch?model=claude-opus&reasoning=medium", null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("claude-opus", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task SwitchModel_MissingReasoning_ReturnsBadRequest()
    {
        // Both query parameters are required — a switch always carries an explicit effort.
        var response = await _client.PostAsync(
            "/api/composer/models/switch?model=claude-opus", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("reasoning", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SwitchModel_MissingModel_ReturnsBadRequest()
    {
        var response = await _client.PostAsync(
            "/api/composer/models/switch?reasoning=medium", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("model", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("turbo")]
    [InlineData("ExtraHigh")]
    [InlineData("3")]
    public async Task SwitchModel_InvalidReasoning_ReturnsBadRequest(string reasoning)
    {
        var response = await _client.PostAsync(
            $"/api/composer/models/switch?model=claude-opus&reasoning={reasoning}",
            null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("extra_high")]
    public async Task SwitchModel_EveryValidReasoning_ReturnsOkAndEchoesEffort(string reasoning)
    {
        var response = await _client.PostAsync(
            $"/api/composer/models/switch?model=claude-opus&reasoning={reasoning}",
            null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("claude-opus", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(reasoning, doc.RootElement.GetProperty("reasoningEffort").GetString());
    }

    [Fact]
    public async Task SwitchModel_ToInvalidModel_ReturnsBadRequest()
    {
        var response = await _client.PostAsync("/api/composer/models/switch?model=unknown-model&reasoning=medium", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var doc = JsonDocument.Parse(json);
        var error = doc.RootElement.GetProperty("error").GetString();
        Assert.Contains("unknown-model", error);
    }

    [Fact]
    public async Task SwitchModel_IsCaseInsensitive()
    {
        var response = await _client.PostAsync("/api/composer/models/switch?model=CLAUDE-OPUS&reasoning=medium", null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("CLAUDE-OPUS", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task SwitchModel_PreservesModelAfterSwitch()
    {
        // Switch to claude-opus
        await _client.PostAsync("/api/composer/models/switch?model=claude-opus&reasoning=medium", null, TestContext.Current.CancellationToken);

        // Verify it's still switched after subsequent request
        var response = await _client.PostAsync("/api/composer/models/switch?model=claude-sonnet-4&reasoning=medium", null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("claude-sonnet-4", doc.RootElement.GetProperty("model").GetString());
    }

    // ── GET /api/composer/models with global config ──

    [Fact]
    public async Task GetModels_WithGlobalAvailableModels_ReturnsGlobalModelNames()
    {
        // Arrange: build app with a HiveConfigFile that has global Models.AvailableModels
        var globalConfig = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "global-model-a" },
                    new ModelEntry { Name = "global-model-b" }
                ]
            }
        };

        await using var fixture = new ComposerHubWithConfigFixture(globalConfig);
        await fixture.InitializeAsync();

        // Act
        var response = await fixture.Client.GetAsync("/api/composer/models", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = JsonDocument.Parse(content);
        var models = json.RootElement.GetProperty("models");

        Assert.Equal(2, models.GetArrayLength());
        var modelsList = models.EnumerateArray().Select(m => m.GetString()!).ToList();
        Assert.Equal("global-model-a", modelsList[0]);
        Assert.Equal("global-model-b", modelsList[1]);

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task GetModels_WithoutGlobalList_ReturnsEmptyCatalog()
    {
        // Arrange: config with no global Models.AvailableModels — the Composer's normalised
        // catalog (GetComposerAvailableModels) is the SOLE authority and yields EMPTY, with
        // no fallback to the startup models.
        var globalConfig = new HiveConfigFile
        {
            Composer = new ComposerConfig
            {
                Model = "composer-primary"
            }
        };

        await using var fixture = new ComposerHubWithConfigFixture(globalConfig);
        await fixture.InitializeAsync();

        // Act
        var response = await fixture.Client.GetAsync("/api/composer/models", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = JsonDocument.Parse(content);
        var models = json.RootElement.GetProperty("models");

        // No global list → the normalised catalog is empty (no fabricated fallback).
        Assert.Equal(0, models.GetArrayLength());

        await fixture.DisposeAsync();
    }

    // ── POST /api/composer/models/switch with global config ──

    [Fact]
    public async Task SwitchModel_WithGlobalList_ValidModel_Succeeds()
    {
        // Arrange: global config restricts available models
        var globalConfig = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "global-model-a" },
                    new ModelEntry { Name = "gpt-4" }
                ]
            }
        };

        await using var fixture = new ComposerHubWithConfigFixture(globalConfig);
        await fixture.InitializeAsync();

        // Act — switch to a model that IS in the global list
        var response = await fixture.Client.PostAsync("/api/composer/models/switch?model=gpt-4&reasoning=medium", null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("gpt-4", doc.RootElement.GetProperty("model").GetString());

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task SwitchModel_WithGlobalList_InvalidModel_ReturnsBadRequest()
    {
        // Arrange: global config restricts available models
        var globalConfig = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "global-model-a" },
                    new ModelEntry { Name = "global-model-b" }
                ]
            }
        };

        await using var fixture = new ComposerHubWithConfigFixture(globalConfig);
        await fixture.InitializeAsync();

        // Act — try to switch to a model that is NOT in the global list, even though
        // it IS in composer.AvailableModels
        var response = await fixture.Client.PostAsync("/api/composer/models/switch?model=claude-sonnet-4&reasoning=medium", null, TestContext.Current.CancellationToken);

        // Assert — should fail because "claude-sonnet-4" is not in the global model list
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var doc = JsonDocument.Parse(content);
        var error = doc.RootElement.GetProperty("error").GetString();
        Assert.Contains("claude-sonnet-4", error);

        await fixture.DisposeAsync();
    }

    // ── Live per-request config reading ──

    [Fact]
    public async Task GetModels_ReflectsMutatedGlobalList_AfterStartup()
    {
        // Arrange: start with two global models
        var globalConfig = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "alpha" },
                    new ModelEntry { Name = "beta" }
                ]
            }
        };

        await using var fixture = new ComposerHubWithConfigFixture(globalConfig);
        await fixture.InitializeAsync();

        // Act 1: verify initial state
        var response1 = await fixture.Client.GetAsync("/api/composer/models", TestContext.Current.CancellationToken);
        response1.EnsureSuccessStatusCode();
        var content1 = await response1.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json1 = JsonDocument.Parse(content1);
        var models1 = json1.RootElement.GetProperty("models");
        Assert.Equal(2, models1.GetArrayLength());

        // Act 2: mutate the global config by adding a new model
        globalConfig.Models!.AvailableModels!.Add(new ModelEntry { Name = "gamma" });

        // Act 3: request again — must reflect the mutated list
        var response2 = await fixture.Client.GetAsync("/api/composer/models", TestContext.Current.CancellationToken);
        response2.EnsureSuccessStatusCode();
        var content2 = await response2.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json2 = JsonDocument.Parse(content2);
        var models2 = json2.RootElement.GetProperty("models");
        Assert.Equal(3, models2.GetArrayLength());
        var names = models2.EnumerateArray().Select(m => m.GetString()!).ToList();
        Assert.Equal(["alpha", "beta", "gamma"], names);

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task SwitchModel_ValidatesAgainstMutatedGlobalList_AfterStartup()
    {
        // Arrange: start with a restricted global list that does not include "gpt-4"
        var globalConfig = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "alpha" },
                    new ModelEntry { Name = "beta" }
                ]
            }
        };

        await using var fixture = new ComposerHubWithConfigFixture(globalConfig);
        await fixture.InitializeAsync();

        // Act 1: "gpt-4" is NOT in global list → should be rejected
        var response1 = await fixture.Client.PostAsync("/api/composer/models/switch?model=gpt-4&reasoning=medium", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response1.StatusCode);

        // Act 2: mutate config to add "gpt-4" to the global list
        globalConfig.Models!.AvailableModels!.Add(new ModelEntry { Name = "gpt-4" });

        // Act 3: "gpt-4" IS now in the global list → should succeed
        var response2 = await fixture.Client.PostAsync("/api/composer/models/switch?model=gpt-4&reasoning=medium", null, TestContext.Current.CancellationToken);
        response2.EnsureSuccessStatusCode();
        var json = await response2.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("gpt-4", doc.RootElement.GetProperty("model").GetString());

        await fixture.DisposeAsync();
    }

    // ── Normalised-catalog endpoint wiring (Slice 1A2a) ──

    /// <summary>
    /// Whitespace-bearing/duplicate raw entries in the global list must NOT leak into the
    /// endpoint listing: the Composer's normalised catalog (GetComposerAvailableModels) is
    /// the sole authority, so the list is trimmed/deduplicated exactly like the backend.
    /// </summary>
    [Fact]
    public async Task GetModels_WhitespaceAndDuplicateRawEntries_ReturnsNormalisedCatalog()
    {
        var globalConfig = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "  model-a  " },
                    new ModelEntry { Name = "model-a" },
                    new ModelEntry { Name = "model-b" },
                    new ModelEntry { Name = "   " },
                    new ModelEntry { Name = "model-b" },
                ]
            }
        };

        await using var fixture = new ComposerHubWithConfigFixture(globalConfig);
        await fixture.InitializeAsync();

        var response = await fixture.Client.GetAsync("/api/composer/models", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = JsonDocument.Parse(content);
        var models = json.RootElement.GetProperty("models");

        // Normalised: trimmed, whitespace-only dropped, duplicates collapsed (first wins).
        var modelsList = models.EnumerateArray().Select(m => m.GetString()!).ToList();
        Assert.Equal(["model-a", "model-b"], modelsList);

        await fixture.DisposeAsync();
    }

    /// <summary>
    /// The switch endpoint's membership check must match the backend SwitchModelAsync
    /// validation exactly: a whitespace-padded raw entry is NOT selectable (the normalised
    /// catalog holds the trimmed name), and the trimmed name IS selectable.
    /// </summary>
    [Fact]
    public async Task SwitchModel_WhitespaceRawEntry_NotSelectable_TrimmedNameIs()
    {
        var globalConfig = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "  model-a  " },
                    new ModelEntry { Name = "model-b" }
                ]
            }
        };

        await using var fixture = new ComposerHubWithConfigFixture(globalConfig);
        await fixture.InitializeAsync();

        // The whitespace-padded raw name is NOT in the normalised catalog → rejected.
        var response1 = await fixture.Client.PostAsync(
            "/api/composer/models/switch?model=%20%20model-a%20%20&reasoning=medium",
            null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response1.StatusCode);

        // The trimmed canonical name IS in the normalised catalog → accepted.
        var response2 = await fixture.Client.PostAsync(
            "/api/composer/models/switch?model=model-a&reasoning=medium",
            null, TestContext.Current.CancellationToken);
        response2.EnsureSuccessStatusCode();

        await fixture.DisposeAsync();
    }

    /// <summary>
    /// Frozen contract (Slice 1B): current-model for a disconnected shell with a non-empty
    /// catalog returns HTTP 200 with {"model":null} — the endpoint NEVER fabricates a value
    /// from the catalog (no FirstOrDefault fallback).
    /// </summary>
    [Fact]
    public async Task CurrentModel_DisconnectedShell_WithCatalog_ReturnsNull()
    {
        var globalConfig = new HiveConfigFile
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

        await using var fixture = new ComposerHubWithConfigFixture(globalConfig);
        await fixture.InitializeAsync();

        var response = await fixture.Client.GetAsync("/api/composer/current-model", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = JsonDocument.Parse(content);
        var model = json.RootElement.GetProperty("model");

        // Frozen contract: null, never the first catalog entry.
        Assert.Equal(JsonValueKind.Null, model.ValueKind);

        await fixture.DisposeAsync();
    }
}

/// <summary>
/// Tests for ComposerHub with null Composer.
/// </summary>
public sealed class ComposerHubNullTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        ((IWebHostBuilder)builder.WebHost).UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        // Map endpoints with null composer - should not throw
        _app.MapComposerEndpoints(
            null!,
            new ComposerFacade(null, NullLogger<ComposerFacade>.Instance),
            config: null);
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app != null!)
            await _app.DisposeAsync();
    }

    [Fact]
    public async Task MapComposerEndpoints_WithNullComposer_Returns404()
    {
        // Endpoints should not be mapped when composer is null
        var response = await _client.GetAsync("/api/composer/models", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

/// <summary>
/// Tests for Composer's compaction model storage — verifies that the
/// <c>compactionModel</c> constructor parameter is correctly stored in the
/// private <c>_compactionModel</c> field.
/// </summary>
public sealed class ComposerCompactionTests : IDisposable
{
    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _store;

    public ComposerCompactionTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    /// <summary>
    /// <see cref="Composer"/> must store the <c>compactionModel</c> constructor
    /// parameter in its private <c>_compactionModel</c> field so that
    /// <c>RecreateAgent()</c> can use it to create a separate compaction client.
    /// </summary>
    [Fact]
    public void Constructor_CompactionModel_StoresValue()
    {
        var composer = new Composer(
            "claude-sonnet-4",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            chatClientFactory: _ => new Mock<IChatClient>().Object,
            compactionModel: "copilot/gpt-5.4-mini");

        var agentServiceField = typeof(Composer)
            .GetField("_agentService", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("_agentService field not found on Composer");
        var agentService = agentServiceField.GetValue(composer)
            ?? throw new InvalidOperationException("_agentService was null");

        var field = agentService.GetType()
            .GetField("_compactionModel", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("_compactionModel field not found on ComposerAgentService");

        Assert.Equal("copilot/gpt-5.4-mini", field.GetValue(agentService));
    }
}

/// <summary>
/// Integration tests verifying that ComposerHub endpoints expose plain model names only —
/// a <c>ModelEntry.ReasoningEffort</c> is never appended to the exposed name.
/// </summary>
public sealed class ComposerHubCompositeModelTests
{
    [Fact]
    public async Task GetModels_ReturnsPlainName_EvenWhenReasoningEffortIsSet()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "copilot/claude-sonnet-4.6", ReasoningEffort = "high" }
                ]
            }
        };

        await using var fixture = new ComposerHubWithConfigFixture(config);
        await fixture.InitializeAsync();

        var response = await fixture.Client.GetAsync("/api/composer/models", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = JsonDocument.Parse(content);
        var models = json.RootElement.GetProperty("models");

        Assert.Equal(1, models.GetArrayLength());
        Assert.Equal("copilot/claude-sonnet-4.6", models.EnumerateArray().First().GetString());

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task GetModels_ReturnsPlainName_WhenReasoningEffortIsNull()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "copilot/claude-sonnet-4.6", ReasoningEffort = null }
                ]
            }
        };

        await using var fixture = new ComposerHubWithConfigFixture(config);
        await fixture.InitializeAsync();

        var response = await fixture.Client.GetAsync("/api/composer/models", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = JsonDocument.Parse(content);
        var models = json.RootElement.GetProperty("models");

        Assert.Equal(1, models.GetArrayLength());
        Assert.Equal("copilot/claude-sonnet-4.6", models.EnumerateArray().First().GetString());

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task GetModels_ReturnsPlainNames_ForMixedReasoningEffortEntries()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "copilot/claude-sonnet-4.6", ReasoningEffort = "high" },
                    new ModelEntry { Name = "gpt-4o" }
                ]
            }
        };

        await using var fixture = new ComposerHubWithConfigFixture(config);
        await fixture.InitializeAsync();

        var response = await fixture.Client.GetAsync("/api/composer/models", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = JsonDocument.Parse(content);
        var models = json.RootElement.GetProperty("models");

        Assert.Equal(2, models.GetArrayLength());
        var modelsList = models.EnumerateArray().Select(m => m.GetString()!).ToList();
        Assert.Equal("copilot/claude-sonnet-4.6", modelsList[0]);
        Assert.Equal("gpt-4o", modelsList[1]);

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task GetModels_ReturnsPlainName_WhenReasoningEffortIsEmpty()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "copilot/claude-sonnet-4.6", ReasoningEffort = "" }
                ]
            }
        };

        await using var fixture = new ComposerHubWithConfigFixture(config);
        await fixture.InitializeAsync();

        var response = await fixture.Client.GetAsync("/api/composer/models", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = JsonDocument.Parse(content);
        var models = json.RootElement.GetProperty("models");

        Assert.Equal(1, models.GetArrayLength());
        Assert.Equal("copilot/claude-sonnet-4.6", models.EnumerateArray().First().GetString());

        await fixture.DisposeAsync();
    }

    /// <summary>
    /// The switch endpoint accepts the plain configured model name, even when the entry
    /// carries a reasoning effort — the effort is never part of the accepted identifier.
    /// </summary>
    [Fact]
    public async Task SwitchModel_AcceptsPlainName_WhenReasoningEffortIsSet()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "gpt-4", ReasoningEffort = "medium" }
                ]
            }
        };

        await using var fixture = new ComposerHubWithConfigFixture(config);
        await fixture.InitializeAsync();

        var response = await fixture.Client.PostAsync(
            "/api/composer/models/switch?model=gpt-4&reasoning=medium", null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("gpt-4", doc.RootElement.GetProperty("model").GetString());

        await fixture.DisposeAsync();
    }

    /// <summary>
    /// A composite "model:effort" string is no longer a valid identifier — the endpoint
    /// only accepts plain configured model names.
    /// </summary>
    [Fact]
    public async Task SwitchModel_RejectsCompositeModelString()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "gpt-4", ReasoningEffort = "medium" }
                ]
            }
        };

        await using var fixture = new ComposerHubWithConfigFixture(config);
        await fixture.InitializeAsync();

        var response = await fixture.Client.PostAsync(
            "/api/composer/models/switch?model=gpt-4:medium&reasoning=medium", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task SwitchModel_RejectsUnknownModel()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "gpt-4", ReasoningEffort = "medium" }
                ]
            }
        };

        await using var fixture = new ComposerHubWithConfigFixture(config);
        await fixture.InitializeAsync();

        var response = await fixture.Client.PostAsync(
            "/api/composer/models/switch?model=gpt-4:high&reasoning=medium", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var doc = JsonDocument.Parse(content);
        var error = doc.RootElement.GetProperty("error").GetString();
        Assert.Contains("gpt-4:high", error);

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task SwitchModel_AcceptsPlainName_WhenReasoningEffortIsNull()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "gpt-4", ReasoningEffort = null }
                ]
            }
        };

        await using var fixture = new ComposerHubWithConfigFixture(config);
        await fixture.InitializeAsync();

        var response = await fixture.Client.PostAsync(
            "/api/composer/models/switch?model=gpt-4&reasoning=medium", null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("gpt-4", doc.RootElement.GetProperty("model").GetString());

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task SwitchModel_IsCaseInsensitive_ForPlainModelName()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "gpt-4", ReasoningEffort = "medium" }
                ]
            }
        };

        await using var fixture = new ComposerHubWithConfigFixture(config);
        await fixture.InitializeAsync();

        var response = await fixture.Client.PostAsync(
            "/api/composer/models/switch?model=GPT-4&reasoning=medium", null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        await fixture.DisposeAsync();
    }

    /// <summary>
    /// Mutating <c>ModelEntry.ReasoningEffort</c> at runtime must never change the exposed
    /// model name — the endpoint always returns the plain name.
    /// </summary>
    [Fact]
    public async Task GetModels_StaysPlain_WhenReasoningEffortIsMutated()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "claude-sonnet-4" }
                ]
            }
        };

        await using var fixture = new ComposerHubWithConfigFixture(config);
        await fixture.InitializeAsync();

        var response1 = await fixture.Client.GetAsync("/api/composer/models", TestContext.Current.CancellationToken);
        response1.EnsureSuccessStatusCode();
        var content1 = await response1.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json1 = JsonDocument.Parse(content1);
        Assert.Equal("claude-sonnet-4", json1.RootElement.GetProperty("models").EnumerateArray().First().GetString());

        // Mutate: add reasoning effort
        config.Models!.AvailableModels![0].ReasoningEffort = "high";

        // The exposed name must stay plain.
        var response2 = await fixture.Client.GetAsync("/api/composer/models", TestContext.Current.CancellationToken);
        response2.EnsureSuccessStatusCode();
        var content2 = await response2.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json2 = JsonDocument.Parse(content2);
        Assert.Equal("claude-sonnet-4", json2.RootElement.GetProperty("models").EnumerateArray().First().GetString());

        await fixture.DisposeAsync();
    }
}

/// <summary>
/// Fixture that creates a test web application with a Composer and an optional HiveConfigFile.
/// Used by integration tests that need to verify the global model list behavior.
/// </summary>
public sealed class ComposerHubWithConfigFixture : IAsyncDisposable
{
    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _store;
    private readonly Composer _composer;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public HttpClient Client => _client;

    public ComposerHubWithConfigFixture(HiveConfigFile? config)
    {
        Config = config;
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);

        _composer = new Composer(
            "claude-sonnet-4",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            availableModels: ["claude-sonnet-4", "claude-opus"],
            hiveConfig: Config,
            chatClientFactory: _ => new Mock<IChatClient>().Object);
    }

    public HiveConfigFile? Config { get; }

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:0");
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<Composer>(_composer);
        // Wire the production global JSON options so enum payloads (reasoningEffort) are
        // serialized exactly as the real host does, rather than with framework defaults.
        Program.AddHiveJsonOptions(builder.Services);
        _app = builder.Build();
        _app.MapComposerEndpoints(
            _composer,
            new ComposerFacade(_composer, NullLogger<ComposerFacade>.Instance),
            Config);
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app != null!)
            await _app.DisposeAsync();
        _dbContext.Dispose();
    }
}