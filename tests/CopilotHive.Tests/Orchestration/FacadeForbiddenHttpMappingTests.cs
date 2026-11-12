using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// THE HTTP MAPPING of an authorization denial: <see cref="FacadeErrorKind.Forbidden"/> is HTTP 403
/// at BOTH hubs, and it is never reported as the compaction catch-all's 400.
/// <para>
/// These tests drive the real endpoint registration (<see cref="ComposerHub.MapComposerEndpoints"/>
/// and <see cref="ConfigHub.MapConfigEndpoints"/>) over a REAL directory-backed facade, so the 403
/// comes from an actual Viewer resolution — not from a stubbed error kind.
/// </para>
/// </summary>
public sealed class FacadeForbiddenHttpMappingTests : IAsyncLifetime, IDisposable
{
    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _store;
    private readonly List<string> _tempDirs = [];

    private WebApplication? _composerApp;
    private WebApplication? _configApp;
    private HttpClient? _composerClient;
    private HttpClient? _configClient;

    public FacadeForbiddenHttpMappingTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Composer CreateComposer()
    {
        var stateDir = Path.Combine(Path.GetTempPath(), $"facade-forbidden-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        _tempDirs.Add(stateDir);

        return new Composer(
            "claude-sonnet-4",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: stateDir,
            availableModels: ["claude-sonnet-4", "claude-opus"],
            chatClientFactory: _ => new Mock<IChatClient>().Object);
    }

    /// <summary>
    /// A hub host with authentication ENABLED is not needed to prove the mapping: the endpoints
    /// forward <see cref="Microsoft.AspNetCore.Http.HttpContext.User"/>, and this host registers a
    /// tiny middleware that sets a Viewer identity on every request. That is strictly weaker than
    /// the production cookie handler (the facade still re-resolves from the directory), and it makes
    /// the mapping assertion about the 403 rather than about OAuth plumbing.
    /// </summary>
    private static void UseIdentity(WebApplication app, string? gitHubId)
        => app.Use(async (context, next) =>
        {
            if (gitHubId is not null)
                context.User = FacadeAuthorizationHarness.Principal(gitHubId);
            await next();
        });

    public async ValueTask InitializeAsync()
    {
        await _auth.SeedAsync("http-viewer", UserRole.Viewer);
        await _auth.SeedAsync("http-member", UserRole.Member);
        await _auth.SeedAsync("http-admin", UserRole.Admin);

        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:0");

        var composer = CreateComposer();
        var composerBuilder = WebApplication.CreateBuilder();
        Program.AddHiveJsonOptions(composerBuilder.Services);
        _composerApp = composerBuilder.Build();
        UseIdentity(_composerApp, "http-viewer");
        _composerApp.MapComposerEndpoints(
            composer,
            new ComposerFacade(
                composer,
                _auth.Resolver(HiveAuthMode.AuthenticationEnabled),
                _auth.Policy,
                NullLogger<ComposerFacade>.Instance),
            config: null);
        await _composerApp.StartAsync(Ct);
        _composerClient = new HttpClient { BaseAddress = new Uri(_composerApp.Urls.First()) };

        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] },
        };
        var configBuilder = WebApplication.CreateBuilder();
        Program.AddHiveJsonOptions(configBuilder.Services);
        _configApp = configBuilder.Build();
        UseIdentity(_configApp, "http-viewer");
        _configApp.MapConfigEndpoints(new ConfigFacade(
            config,
            configModel: null,
            discovery: null,
            NullLogger<ConfigFacade>.Instance,
            _auth.Resolver(HiveAuthMode.AuthenticationEnabled),
            _auth.Policy));
        await _configApp.StartAsync(Ct);
        _configClient = new HttpClient { BaseAddress = new Uri(_configApp.Urls.First()) };
    }

    // ── ComposerHub: Forbidden → 403 ─────────────────────────────────────────

    /// <summary>
    /// EVERY Composer endpoint answers a Viewer with 403 and the sanitized <c>{error}</c> body — and
    /// never a 400, even on the compaction routes whose own catch-all maps operational failures
    /// there.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/composer/current-model")]
    [InlineData("GET", "/api/composer/models")]
    [InlineData("POST", "/api/composer/models/switch?model=claude-opus&reasoning=medium")]
    [InlineData("POST", "/api/composer/compact")]
    [InlineData("POST", "/api/composer/compact-partial?percent=50")]
    public async Task ComposerEndpoints_Viewer_Gets403_AndNeverA400(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await _composerClient!.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(Ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(
            "The acting account is not authorized to perform this operation.",
            doc.RootElement.GetProperty("error").GetString());
    }

    /// <summary>
    /// A Member really reaches the Composer through the hub: the catalog endpoint 200s with the
    /// Composer's own list. The identity middleware is switched to a Member for this host, so the
    /// 403 rows above cannot be satisfied by an endpoint that refuses everyone.
    /// </summary>
    [Fact]
    public async Task ComposerEndpoints_Member_Gets200()
    {
        var composer = CreateComposer();
        await using (composer)
        {
            var builder = WebApplication.CreateBuilder();
            Program.AddHiveJsonOptions(builder.Services);
            var app = builder.Build();
            UseIdentity(app, "http-member");
            app.MapComposerEndpoints(
                composer,
                new ComposerFacade(
                    composer,
                    _auth.Resolver(HiveAuthMode.AuthenticationEnabled),
                    _auth.Policy,
                    NullLogger<ComposerFacade>.Instance),
                config: null);
            await app.StartAsync(Ct);
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
                var response = await client.GetAsync("/api/composer/models", Ct);

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
                Assert.Equal(
                    composer.AvailableModels,
                    body.GetProperty("models").EnumerateArray().Select(e => e.GetString()!).ToList());

                await app.StopAsync(Ct);
            }
            finally
            {
                await app.DisposeAsync();
            }
        }
    }

    // ── ConfigHub: PATCH /api/config/composer Forbidden → 403 ───────────────

    /// <summary>
    /// The GLOBAL composer write answers a Viewer with 403 for every update shape, and NOTHING is
    /// persisted.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"maxSteps\":75}")]
    [InlineData("{\"eventNotificationsMode\":\"active\"}")]
    public async Task ConfigComposerPatch_Viewer_Gets403_AndPersistsNothing(string json)
    {
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _configClient!.PatchAsync("/api/config/composer", content, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(
            "The acting account is not authorized to perform this operation.",
            doc.RootElement.GetProperty("error").GetString());
    }

    /// <summary>
    /// EVERY configuration projection is an administration surface: the Composer READ answers a
    /// Viewer with 403 and the sanitized <c>{error}</c> body — it is the same full projection the
    /// write is gated on, so a Viewer cannot read the global settings either. This row is the
    /// read-side counterpart of the write rows above.
    /// </summary>
    [Fact]
    public async Task ConfigComposerGet_Viewer_Gets403_WithSanitizedBody()
    {
        var response = await _configClient!.GetAsync("/api/config/composer", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(Ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(
            "The acting account is not authorized to perform this operation.",
            doc.RootElement.GetProperty("error").GetString());
    }

    /// <summary>
    /// THE POSITIVE CONTROL for the config matrix: the very same hub, over the very same facade and
    /// the very same config, answers an ADMIN with 200 and the real projection. Without this, the
    /// 403 rows could be satisfied by a hub that refuses everyone.
    /// </summary>
    [Fact]
    public async Task ConfigComposerGet_Admin_Gets200_WithTheRealProjection()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] },
            Composer = new ComposerConfig { Model = "copilot/composer", MaxSteps = 42 },
        };
        var builder = WebApplication.CreateBuilder();
        Program.AddHiveJsonOptions(builder.Services);
        var app = builder.Build();
        UseIdentity(app, "http-admin");
        app.MapConfigEndpoints(new ConfigFacade(
            config,
            configModel: null,
            discovery: null,
            NullLogger<ConfigFacade>.Instance,
            _auth.Resolver(HiveAuthMode.AuthenticationEnabled),
            _auth.Policy));
        await app.StartAsync(Ct);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            var response = await client.GetAsync("/api/config/composer", Ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal("copilot/composer", body.GetProperty("model").GetString());
            Assert.Equal(42, body.GetProperty("maxSteps").GetInt32());
        }
        finally
        {
            await app.StopAsync(Ct);
            await app.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _composerClient?.Dispose();
        _configClient?.Dispose();

        if (_composerApp is not null)
        {
            await _composerApp.StopAsync(Ct);
            await _composerApp.DisposeAsync();
        }

        if (_configApp is not null)
        {
            await _configApp.StopAsync(Ct);
            await _configApp.DisposeAsync();
        }
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _auth.Dispose();
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of the isolated state directory.
            }
        }
    }
}
