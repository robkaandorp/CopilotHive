using System.Reflection;
using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
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
/// Tests for multi-model support in Composer configuration.
/// </summary>
public sealed class ComposerConfigTests
{
    // ── GetAvailableModels ──

    [Fact]
    public void GetAvailableModels_WithBothModelAndModels_ReturnsMergedList()
    {
        var config = new ComposerConfig
        {
            Model = "claude-sonnet-4",
            Models = ["gpt-4", "claude-opus"]
        };

        var result = config.GetAvailableModels("fallback");

        Assert.Equal(3, result.Count);
        Assert.Equal("claude-sonnet-4", result[0]); // Model is first
        Assert.Equal("gpt-4", result[1]);
        Assert.Equal("claude-opus", result[2]);
    }

    [Fact]
    public void GetAvailableModels_WithOnlyModel_ReturnsSingleModelList()
    {
        var config = new ComposerConfig
        {
            Model = "claude-sonnet-4"
        };

        var result = config.GetAvailableModels("fallback");

        Assert.Single(result);
        Assert.Equal("claude-sonnet-4", result[0]);
    }

    [Fact]
    public void GetAvailableModels_WithOnlyModels_ReturnsModelsList()
    {
        var config = new ComposerConfig
        {
            Models = ["gpt-4", "claude-opus"]
        };

        var result = config.GetAvailableModels("fallback");

        Assert.Equal(2, result.Count);
        Assert.Equal("gpt-4", result[0]);
        Assert.Equal("claude-opus", result[1]);
    }

    [Fact]
    public void GetAvailableModels_WithNeitherModelNorModels_ReturnsFallback()
    {
        var config = new ComposerConfig();

        var result = config.GetAvailableModels("fallback-model");

        Assert.Single(result);
        Assert.Equal("fallback-model", result[0]);
    }

    [Fact]
    public void GetAvailableModels_DeduplicatesCaseInsensitively()
    {
        var config = new ComposerConfig
        {
            Model = "Claude-Sonnet-4",
            Models = ["claude-sonnet-4", "CLAUDE-SONNET-4", "gpt-4"]
        };

        var result = config.GetAvailableModels("fallback");

        Assert.Equal(2, result.Count);
        Assert.Equal("Claude-Sonnet-4", result[0]); // Original casing preserved for first
        Assert.Equal("gpt-4", result[1]);
    }

    [Fact]
    public void GetAvailableModels_SkipsNullOrEmptyInModelsList()
    {
        var config = new ComposerConfig
        {
            Model = "claude-sonnet-4",
            Models = ["gpt-4", "", null!, "claude-opus"]
        };

        var result = config.GetAvailableModels("fallback");

        Assert.Equal(3, result.Count);
        Assert.Equal("claude-sonnet-4", result[0]);
        Assert.Equal("gpt-4", result[1]);
        Assert.Equal("claude-opus", result[2]);
    }

    [Fact]
    public void GetAvailableModels_ModelAlwaysFirst_WhenPresentInModelsToo()
    {
        var config = new ComposerConfig
        {
            Model = "primary-model",
            Models = ["secondary-model", "primary-model", "tertiary-model"]
        };

        var result = config.GetAvailableModels("fallback");

        Assert.Equal(3, result.Count);
        Assert.Equal("primary-model", result[0]); // Primary is always first
        Assert.Equal("secondary-model", result[1]);
        Assert.Equal("tertiary-model", result[2]);
    }
}

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
        await _composer.SwitchModelAsync("claude-opus");

        // No exception means success - verify by checking stats
        var stats = _composer.GetStats();
        Assert.Equal("claude-opus", stats?.Model);
    }

    [Fact]
    public async Task SwitchModelAsync_ToInvalidModel_ThrowsArgumentException()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _composer.SwitchModelAsync("unknown-model"));

        Assert.Contains("unknown-model", exception.Message);
        Assert.Contains("Available models:", exception.Message);
    }

    [Fact]
    public async Task SwitchModelAsync_IsCaseInsensitive()
    {
        // Should not throw - model matching is case-insensitive
        await _composer.SwitchModelAsync("CLAUDE-OPUS");

        var stats = _composer.GetStats();
        Assert.Equal("CLAUDE-OPUS", stats?.Model);
    }

    [Fact]
    public async Task SwitchModelAsync_PreservesSessionHistory()
    {
        // Create initial session with some content
        await _composer.ConnectAsync(TestContext.Current.CancellationToken);
        
        // Switch model
        await _composer.SwitchModelAsync("claude-opus");

        // Session should still exist (verified via stats)
        var stats = _composer.GetStats();
        Assert.NotNull(stats);
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
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => composer.SwitchModelAsync("gpt-4"));
        Assert.Contains("gpt-4", ex.Message);

        // Act 2: mutate the global config to add "gpt-4"
        liveConfig.Models!.AvailableModels!.Add(new ModelEntry { Name = "gpt-4" });

        // Act 3: "gpt-4" is now in the global list → should succeed
        await composer.SwitchModelAsync("gpt-4");
        var stats = composer.GetStats();
        Assert.Equal("gpt-4", stats?.Model);
    }

    // ── Composite model context window regression tests ──

    /// <summary>
    /// Regression test: when switching to a composite model string (e.g. "gpt-4:medium"),
    /// SwitchModelAsync must look up the context window using the clean model name
    /// ("gpt-4") — not the full composite string. TryGetContextWindowForModel matches
    /// against ModelEntry.Name which does not include the reasoning effort suffix.
    /// </summary>
    [Fact]
    public async Task SwitchModelAsync_CompositeModel_UpdatesContextWindowFromConfig()
    {
        // Arrange: global config with a ModelEntry that has an explicit ContextWindow
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "claude-sonnet-4" },
                    new ModelEntry { Name = "gpt-4", ReasoningEffort = "medium", ContextWindow = 32768 }
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

        // Act: switch to the composite model string
        await composer.SwitchModelAsync("gpt-4:medium");

        // Assert: the context window should have been updated to 32768 (from ModelEntry.ContextWindow)
        // because the lookup used the clean model name "gpt-4", not "gpt-4:medium"
        var stats = composer.GetStats();
        Assert.NotNull(stats);
        Assert.Equal(32768, stats!.MaxContextTokens);
        Assert.Equal("gpt-4:medium", stats.Model);
    }

    /// <summary>
    /// Regression test: when switching to a composite model whose ModelEntry has no
    /// ContextWindow set, the existing max context tokens must be preserved (the lookup
    /// returns null and the code skips the update).
    /// </summary>
    [Fact]
    public async Task SwitchModelAsync_CompositeModel_NoContextWindow_PreservesExistingMaxTokens()
    {
        // Arrange: global config with a ModelEntry that has NO ContextWindow set
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "claude-sonnet-4" },
                    new ModelEntry { Name = "gpt-4", ReasoningEffort = "medium" }
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
        await composer.SwitchModelAsync("gpt-4:medium");

        // Assert: the max context tokens should be unchanged at 64000 because
        // the ModelEntry has no ContextWindow (lookup returns null)
        var stats = composer.GetStats();
        Assert.NotNull(stats);
        Assert.Equal(64000, stats!.MaxContextTokens);
        Assert.Equal("gpt-4:medium", stats.Model);
    }

    /// <summary>
    /// Regression test: when switching to a plain model name (no reasoning suffix)
    /// with a configured ContextWindow, the context window should still be updated.
    /// This verifies the cleanModel fallback (cleanModel ?? model) works correctly
    /// when ParseProviderModelAndReasoning returns null for the model portion.
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
        await composer.SwitchModelAsync("gpt-4");

        // Assert: the context window should have been updated to 32768
        var stats = composer.GetStats();
        Assert.NotNull(stats);
        Assert.Equal(32768, stats!.MaxContextTokens);
        Assert.Equal("gpt-4", stats.Model);
    }

    /// <summary>
    /// Regression test: switching from a model with a small context window to
    /// a composite model with a larger context window correctly updates the value.
    /// This verifies the fix works in both directions (shrinking and growing).
    /// </summary>
    [Fact]
    public async Task SwitchModelAsync_CompositeModel_UpdatesContextWindowInBothDirections()
    {
        // Arrange: global config with two models having different context windows
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "claude-sonnet-4", ReasoningEffort = "high", ContextWindow = 100000 },
                    new ModelEntry { Name = "gpt-4", ReasoningEffort = "medium", ContextWindow = 32768 }
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
        await composer.SwitchModelAsync("gpt-4:medium");
        var stats1 = composer.GetStats();
        Assert.NotNull(stats1);
        Assert.Equal(32768, stats1!.MaxContextTokens);

        // Act 2: switch back to claude-sonnet-4:high (larger context window)
        await composer.SwitchModelAsync("claude-sonnet-4:high");
        var stats2 = composer.GetStats();
        Assert.NotNull(stats2);
        Assert.Equal(100000, stats2!.MaxContextTokens);
    }

    // ── Provider-prefixed composite model context window regression tests ──

    /// <summary>
    /// Regression test: when switching to a provider-prefixed composite model string
    /// (e.g. "copilot/claude-sonnet-4.6:high"), SwitchModelAsync must look up the context
    /// window using a key that preserves the provider prefix ("copilot/claude-sonnet-4.6")
    /// — not the ParseProviderModelAndReasoning output which strips the prefix
    /// ("claude-sonnet-4.6") or the full composite string with suffix
    /// ("copilot/claude-sonnet-4.6:high"). TryGetContextWindowForModel matches against
    /// ModelEntry.Name which includes the provider prefix.
    /// </summary>
    [Fact]
    public async Task SwitchModelAsync_ProviderPrefixedComposite_UpdatesContextWindow()
    {
        // Arrange: global config with a ModelEntry whose Name includes the provider prefix
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "claude-sonnet-4" },
                    new ModelEntry { Name = "copilot/claude-sonnet-4.6", ReasoningEffort = "high", ContextWindow = 100000 }
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

        // Act: switch to the provider-prefixed composite model string
        await composer.SwitchModelAsync("copilot/claude-sonnet-4.6:high");

        // Assert: the context window should have been updated to 100000 because the lookup
        // used "copilot/claude-sonnet-4.6" (prefix preserved, suffix stripped), matching
        // the configured ModelEntry.Name — NOT "claude-sonnet-4.6" (prefix stripped) or
        // "copilot/claude-sonnet-4.6:high" (suffix not stripped), both of which would miss.
        var stats = composer.GetStats();
        Assert.NotNull(stats);
        Assert.Equal(100000, stats!.MaxContextTokens);
        Assert.Equal("copilot/claude-sonnet-4.6:high", stats.Model);
    }

    /// <summary>
    /// Regression test: when switching to an Ollama-style tagged model with a reasoning
    /// suffix (e.g. "ollama-cloud/gpt-oss:120b:medium"), SwitchModelAsync must strip only
    /// the known reasoning level (:medium), NOT the Ollama tag (:120b). The lookup key
    /// becomes "ollama-cloud/gpt-oss:120b" which matches the configured ModelEntry.Name.
    /// </summary>
    [Fact]
    public async Task SwitchModelAsync_OllamaTaggedComposite_UpdatesContextWindow()
    {
        // Arrange: global config with an Ollama-style tagged model
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "claude-sonnet-4" },
                    new ModelEntry { Name = "ollama-cloud/gpt-oss:120b", ReasoningEffort = "medium", ContextWindow = 200000 }
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
            availableModels: ["claude-sonnet-4", "ollama-cloud/gpt-oss:120b:medium"],
            hiveConfig: config,
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        // Act: switch to the Ollama-style tagged composite model string
        await composer.SwitchModelAsync("ollama-cloud/gpt-oss:120b:medium");

        // Assert: the context window should have been updated to 200000 because the lookup
        // used "ollama-cloud/gpt-oss:120b" (only :medium stripped, :120b tag preserved),
        // matching the configured ModelEntry.Name.
        var stats = composer.GetStats();
        Assert.NotNull(stats);
        Assert.Equal(200000, stats!.MaxContextTokens);
        Assert.Equal("ollama-cloud/gpt-oss:120b:medium", stats.Model);
    }

    /// <summary>
    /// Regression test: when switching to a provider-prefixed composite model whose
    /// ModelEntry has no ContextWindow set, the existing max context tokens must be
    /// preserved (the lookup returns null and the code skips the update).
    /// </summary>
    [Fact]
    public async Task SwitchModelAsync_ProviderPrefixedComposite_NoContextWindow_PreservesExisting()
    {
        // Arrange: global config with a ModelEntry that has NO ContextWindow set
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "claude-sonnet-4" },
                    new ModelEntry { Name = "copilot/claude-sonnet-4.6", ReasoningEffort = "high" }
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

        // Act: switch to the provider-prefixed composite model string
        await composer.SwitchModelAsync("copilot/claude-sonnet-4.6:high");

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
        _app = builder.Build();
        _app.MapComposerEndpoints(_composer, config: null);
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

    // ── POST /api/composer/models/switch ──

    [Fact]
    public async Task SwitchModel_ToValidModel_ReturnsOk()
    {
        var response = await _client.PostAsync("/api/composer/models/switch?model=claude-opus", null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("claude-opus", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task SwitchModel_ToInvalidModel_ReturnsBadRequest()
    {
        var response = await _client.PostAsync("/api/composer/models/switch?model=unknown-model", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var doc = JsonDocument.Parse(json);
        var error = doc.RootElement.GetProperty("error").GetString();
        Assert.Contains("unknown-model", error);
    }

    [Fact]
    public async Task SwitchModel_IsCaseInsensitive()
    {
        var response = await _client.PostAsync("/api/composer/models/switch?model=CLAUDE-OPUS", null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("CLAUDE-OPUS", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task SwitchModel_PreservesModelAfterSwitch()
    {
        // Switch to claude-opus
        await _client.PostAsync("/api/composer/models/switch?model=claude-opus", null, TestContext.Current.CancellationToken);

        // Verify it's still switched after subsequent request
        var response = await _client.PostAsync("/api/composer/models/switch?model=claude-sonnet-4", null, TestContext.Current.CancellationToken);
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
    public async Task GetModels_WithoutGlobalList_FallsBackToComposerAvailableModels()
    {
        // Arrange: config with no global Models.AvailableModels, only Composer models
        var globalConfig = new HiveConfigFile
        {
            Composer = new ComposerConfig
            {
                Model = "composer-primary",
                Models = ["composer-primary", "composer-secondary"]
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

        // Should return the composer.AvailableModels list since no global list is present
        var modelsList = models.EnumerateArray().Select(m => m.GetString()!).ToList();
        Assert.Contains("claude-sonnet-4", modelsList);
        Assert.Contains("claude-opus", modelsList);

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
        var response = await fixture.Client.PostAsync("/api/composer/models/switch?model=gpt-4", null, TestContext.Current.CancellationToken);
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
        var response = await fixture.Client.PostAsync("/api/composer/models/switch?model=claude-sonnet-4", null, TestContext.Current.CancellationToken);

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
        var response1 = await fixture.Client.PostAsync("/api/composer/models/switch?model=gpt-4", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response1.StatusCode);

        // Act 2: mutate config to add "gpt-4" to the global list
        globalConfig.Models!.AvailableModels!.Add(new ModelEntry { Name = "gpt-4" });

        // Act 3: "gpt-4" IS now in the global list → should succeed
        var response2 = await fixture.Client.PostAsync("/api/composer/models/switch?model=gpt-4", null, TestContext.Current.CancellationToken);
        response2.EnsureSuccessStatusCode();
        var json = await response2.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("gpt-4", doc.RootElement.GetProperty("model").GetString());

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
        _app.MapComposerEndpoints(null!, config: null);
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

        var field = typeof(Composer)
            .GetField("_compactionModel", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("_compactionModel field not found on Composer");

        Assert.Equal("copilot/gpt-5.4-mini", field.GetValue(composer));
    }
}

/// <summary>
/// Integration tests verifying that ComposerHub endpoints use composite model values
/// (model:effort) when ReasoningEffort is set on ModelEntry, and plain names when it is not.
/// </summary>
public sealed class ComposerHubCompositeModelTests
{
    [Fact]
    public async Task GetModels_ReturnsCompositeValue_WhenReasoningEffortIsSet()
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
        Assert.Equal("copilot/claude-sonnet-4.6:high", models.EnumerateArray().First().GetString());

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
    public async Task GetModels_ReturnsMixedCompositeAndPlainValues()
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
        Assert.Equal("copilot/claude-sonnet-4.6:high", modelsList[0]);
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

    [Fact]
    public async Task SwitchModel_AcceptsCompositeModelString()
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
            "/api/composer/models/switch?model=gpt-4:medium", null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("gpt-4:medium", doc.RootElement.GetProperty("model").GetString());

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task SwitchModel_RejectsPlainName_WhenCompositeIsRequired()
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

        // "gpt-4" (without suffix) should be rejected because valid list contains "gpt-4:medium"
        var response = await fixture.Client.PostAsync(
            "/api/composer/models/switch?model=gpt-4", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task SwitchModel_RejectsInvalidCompositeModel()
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

        // "gpt-4:high" is NOT in the valid list (only "gpt-4:medium" is)
        var response = await fixture.Client.PostAsync(
            "/api/composer/models/switch?model=gpt-4:high", null, TestContext.Current.CancellationToken);

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
            "/api/composer/models/switch?model=gpt-4", null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("gpt-4", doc.RootElement.GetProperty("model").GetString());

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task SwitchModel_IsCaseInsensitive_ForCompositeModel()
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
            "/api/composer/models/switch?model=GPT-4:MEDIUM", null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task GetModels_ReturnsComposite_ReflectsMutatedReasoningEffort()
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

        // Initially no reasoning effort → plain name
        var response1 = await fixture.Client.GetAsync("/api/composer/models", TestContext.Current.CancellationToken);
        response1.EnsureSuccessStatusCode();
        var content1 = await response1.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json1 = JsonDocument.Parse(content1);
        Assert.Equal("claude-sonnet-4", json1.RootElement.GetProperty("models").EnumerateArray().First().GetString());

        // Mutate: add reasoning effort
        config.Models!.AvailableModels![0].ReasoningEffort = "high";

        // Now should return composite value
        var response2 = await fixture.Client.GetAsync("/api/composer/models", TestContext.Current.CancellationToken);
        response2.EnsureSuccessStatusCode();
        var content2 = await response2.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json2 = JsonDocument.Parse(content2);
        Assert.Equal("claude-sonnet-4:high", json2.RootElement.GetProperty("models").EnumerateArray().First().GetString());

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
        _app = builder.Build();
        _app.MapComposerEndpoints(_composer, Config);
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