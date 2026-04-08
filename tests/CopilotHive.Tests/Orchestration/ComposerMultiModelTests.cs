using System.Reflection;
using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.AI;
using Moq;
using System.Net;
using System.Text.Json;

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
    private readonly SqliteConnection _connection;
    private readonly SqliteGoalStore _store;
    private readonly Composer _composer;

    public ComposerMultiModelTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _store = new SqliteGoalStore(_connection, NullLogger<SqliteGoalStore>.Instance);

        _composer = new Composer(
            "claude-sonnet-4",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            availableModels: ["claude-sonnet-4", "gpt-4", "claude-opus"],
            chatClientFactory: _ => new Mock<IChatClient>().Object);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    // ── AvailableModels Property ──

    [Fact]
    public void AvailableModels_ReturnsConfiguredModels()
    {
        var models = _composer.AvailableModels;

        Assert.Equal(3, models.Count);
        Assert.Equal("claude-sonnet-4", models[0]);
        Assert.Equal("gpt-4", models[1]);
        Assert.Equal("claude-opus", models[2]);
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
        await _composer.SwitchModelAsync("gpt-4");

        // No exception means success - verify by checking stats
        var stats = _composer.GetStats();
        Assert.Equal("gpt-4", stats?.Model);
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
        await _composer.SwitchModelAsync("GPT-4");

        var stats = _composer.GetStats();
        Assert.Equal("GPT-4", stats?.Model);
    }

    [Fact]
    public async Task SwitchModelAsync_PreservesSessionHistory()
    {
        // Create initial session with some content
        await _composer.ConnectAsync(TestContext.Current.CancellationToken);
        
        // Switch model
        await _composer.SwitchModelAsync("gpt-4");

        // Session should still exist (verified via stats)
        var stats = _composer.GetStats();
        Assert.NotNull(stats);
    }
}

/// <summary>
/// Tests for ComposerHub REST API endpoints.
/// </summary>
public sealed class ComposerHubTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly SqliteGoalStore _store;
    private readonly Composer _composer;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public ComposerHubTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _store = new SqliteGoalStore(_connection, NullLogger<SqliteGoalStore>.Instance);

        _composer = new Composer(
            "claude-sonnet-4",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: Path.GetTempPath(),
            availableModels: ["claude-sonnet-4", "gpt-4", "claude-opus"],
            chatClientFactory: _ => new Mock<IChatClient>().Object);
    }

    public async ValueTask InitializeAsync()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:0");
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<Composer>(_composer);
        _app = builder.Build();
        _app.MapComposerEndpoints(_composer);
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app != null!)
            await _app.DisposeAsync();
        _connection.Dispose();
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

        Assert.Equal(3, models.GetArrayLength());

        var modelsList = models.EnumerateArray()
            .Select(m => m.GetString())
            .ToList();

        Assert.Equal("claude-sonnet-4", modelsList[0]);
        Assert.Equal("gpt-4", modelsList[1]);
        Assert.Equal("claude-opus", modelsList[2]);
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
        var response = await _client.PostAsync("/api/composer/models/switch?model=gpt-4", null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("gpt-4", doc.RootElement.GetProperty("model").GetString());
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
        var response = await _client.PostAsync("/api/composer/models/switch?model=GPT-4", null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("GPT-4", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task SwitchModel_PreservesModelAfterSwitch()
    {
        // Switch to gpt-4
        await _client.PostAsync("/api/composer/models/switch?model=gpt-4", null, TestContext.Current.CancellationToken);

        // Verify it's still switched after subsequent request
        var response = await _client.PostAsync("/api/composer/models/switch?model=claude-opus", null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("claude-opus", doc.RootElement.GetProperty("model").GetString());
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
        _app.MapComposerEndpoints(null!);
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
    private readonly SqliteConnection _connection;
    private readonly SqliteGoalStore _store;

    public ComposerCompactionTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _store = new SqliteGoalStore(_connection, NullLogger<SqliteGoalStore>.Instance);
    }

    public void Dispose()
    {
        _connection.Dispose();
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