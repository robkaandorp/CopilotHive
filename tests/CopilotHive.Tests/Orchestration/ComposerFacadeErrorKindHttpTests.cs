using System.Net;
using System.Security.Claims;
using System.Text.Json;

using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// THE ENDPOINT-LEVEL API CONTRACT of the Composer hub after the additive recovery kinds: the
/// facade's <see cref="FacadeErrorKind.ServiceUnavailable"/> is HTTP 503 and its
/// <see cref="FacadeErrorKind.Conflict"/> is HTTP 409 — and the EXISTING mappings (400, 403 and the
/// host's 401) are unchanged.
/// <para>
/// The hub is driven through the REAL endpoint registration in TWO shapes:
/// <list type="bullet">
/// <item>with an explicit facade OVERRIDE (the mapping rows), and</item>
/// <item>with NO override, so the facade is resolved PER REQUEST from
/// <c>HttpContext.RequestServices</c> (the resolution rows).</item>
/// </list>
/// </para>
/// </summary>
public sealed class ComposerFacadeErrorKindHttpTests : IAsyncLifetime
{
    private const string DenialMessage = "The acting account is not authorized to perform this operation.";

    private readonly CopilotHiveDbContext _dbContext = CopilotHiveDbContext.CreateInMemory();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private Composer _composer = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:0");

        _composer = new Composer(
            "claude-sonnet-4",
            NullLogger<Composer>.Instance,
            new GoalStore(_dbContext, NullLogger<GoalStore>.Instance),
            stateDir: Path.GetTempPath(),
            availableModels: ["claude-sonnet-4"],
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        // The host registers the facade SCOPED (the production lifetime) and maps the endpoints with
        // NO override, so each handler must take it from the REQUEST's own scope.
        var builder = WebApplication.CreateBuilder();
        Program.AddHiveJsonOptions(builder.Services);
        builder.Services.AddScoped<IComposerFacade>(_ => new ScopedMarkerFacade());
        _app = builder.Build();
        _app.MapComposerEndpoints(_composer, composerFacade: null, config: null);
        await _app.StartAsync(Ct);
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    // ── The additive recovery kinds ────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="FacadeErrorKind.ServiceUnavailable"/> is HTTP 503 with a problem-details body
    /// carrying the facade's message — on EVERY Composer endpoint, not only the mutation.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/composer/current-model")]
    [InlineData("GET", "/api/composer/models")]
    [InlineData("POST", "/api/composer/models/switch?model=m&reasoning=high")]
    [InlineData("POST", "/api/composer/compact")]
    [InlineData("POST", "/api/composer/compact-partial?percent=50")]
    public async Task ServiceUnavailable_Is503_WithAProblemDetailsBody_OnEveryEndpoint(string method, string path)
    {
        const string message = "The Composer runtime for this account is not available right now.";
        await using var host = await StubHostAsync(new StubFacade
        {
            Kind = FacadeErrorKind.ServiceUnavailable,
            Error = message,
        });

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await host.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains(message, body, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(503, doc.RootElement.GetProperty("status").GetInt32());
    }

    /// <summary>
    /// <see cref="FacadeErrorKind.Conflict"/> is HTTP 409 with the <c>{error}</c> body shape — the
    /// safe retry/requery answer for a removed runtime — on EVERY Composer endpoint.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/composer/current-model")]
    [InlineData("GET", "/api/composer/models")]
    [InlineData("POST", "/api/composer/models/switch?model=m&reasoning=high")]
    [InlineData("POST", "/api/composer/compact")]
    [InlineData("POST", "/api/composer/compact-partial?percent=50")]
    public async Task Conflict_Is409_WithTheErrorBody_OnEveryEndpoint(string method, string path)
    {
        const string message = "The Composer runtime for this account was removed while the request was in flight.";
        await using var host = await StubHostAsync(new StubFacade
        {
            Kind = FacadeErrorKind.Conflict,
            Error = message,
        });

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await host.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(message, doc.RootElement.GetProperty("error").GetString());
    }

    // ── The existing mappings are unchanged ────────────────────────────────────────────────────

    /// <summary>
    /// THE EXISTING MAPPINGS ARE UNCHANGED: <see cref="FacadeErrorKind.BadRequest"/> stays 400
    /// with a <c>{error}</c> body, and <see cref="FacadeErrorKind.Forbidden"/> stays 403 with a
    /// <c>{error}</c> body — and a denial is never remapped into the new 503/409 answers.
    /// </summary>
    [Fact]
    public async Task BadRequestAndForbidden_KeepTheirExistingMappings()
    {
        await using (var badRequest = await StubHostAsync(new StubFacade
        {
            Kind = FacadeErrorKind.BadRequest,
            Error = "The 'model' query parameter is required.",
        }))
        {
            var response = await badRequest.Client.GetAsync("/api/composer/models", Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal(
                "The 'model' query parameter is required.",
                doc.RootElement.GetProperty("error").GetString());
        }

        await using (var forbidden = await StubHostAsync(new StubFacade
        {
            Kind = FacadeErrorKind.Forbidden,
            Error = DenialMessage,
        }))
        {
            var response = await forbidden.Client.GetAsync("/api/composer/models", Ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.NotEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.NotEqual(HttpStatusCode.Conflict, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal(DenialMessage, doc.RootElement.GetProperty("error").GetString());
        }
    }

    /// <summary>
    /// <see cref="FacadeErrorKind.NotConfigured"/> keeps its 503 problem-details mapping: the new
    /// ServiceUnavailable kind did NOT take it over, and it was not remapped into a 409.
    /// </summary>
    [Fact]
    public async Task NotConfigured_KeepsIts503ProblemDetailsMapping()
    {
        await using var host = await StubHostAsync(new StubFacade
        {
            Kind = FacadeErrorKind.NotConfigured,
            Error = "Composer is not available.",
        });

        var response = await host.Client.GetAsync("/api/composer/models", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(503, doc.RootElement.GetProperty("status").GetInt32());
    }

    // ── Per-request resolution ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE FACADE IS RESOLVED PER REQUEST, FROM THE REQUEST'S OWN SCOPE: the host registers it
    /// SCOPED, maps the endpoints with NO override, and the response is served by that instance —
    /// the payload identifies it.
    /// </summary>
    [Fact]
    public async Task TheFacade_IsResolvedFromTheRequestsOwnScope()
    {
        var response = await _client.GetAsync("/api/composer/current-model", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(ScopedMarkerFacade.ModelName, doc.RootElement.GetProperty("model").GetString());
    }

    /// <summary>
    /// EVERY ENDPOINT RESOLVES THE SCOPED FACADE: no handler kept a captured instance, so no
    /// endpoint can serve a stale facade.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/composer/current-model")]
    [InlineData("GET", "/api/composer/models")]
    [InlineData("POST", "/api/composer/models/switch?model=m&reasoning=high")]
    [InlineData("POST", "/api/composer/compact")]
    [InlineData("POST", "/api/composer/compact-partial?percent=50")]
    public async Task EveryEndpoint_ResolvesTheScopedFacade(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await _client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// THE HUB NEVER RESOLVES THE FACADE FROM THE ROOT PROVIDER: the source resolves it from
    /// <c>HttpContext.RequestServices</c>, so no startup closure can capture a scoped service.
    /// </summary>
    [Fact]
    public void TheHub_ResolvesTheFacadeFromRequestServices_NotFromTheRootProvider()
    {
        var source = TestSourceScanner.StripNonCode(File.ReadAllText(
            Path.Combine(TestRepoRoot.Find(), "src", "CopilotHive", "Orchestration", "ComposerHub.cs"))
            .ReplaceLineEndings("\n"));

        Assert.Contains("context.RequestServices.GetRequiredService<IComposerFacade>()", source, StringComparison.Ordinal);
        // No root-provider resolution of the facade anywhere in the hub.
        Assert.DoesNotContain("routes.Services.GetRequiredService<IComposerFacade>()", source, StringComparison.Ordinal);
    }

    // ── Fixtures ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Boots a throwaway host with the supplied facade as the explicit override. The returned
    /// handle disposes the host, so an assertion failure cannot leak a listening server.
    /// </summary>
    private async Task<StubHost> StubHostAsync(IComposerFacade facade)
    {
        var builder = WebApplication.CreateBuilder();
        Program.AddHiveJsonOptions(builder.Services);
        var app = builder.Build();
        app.MapComposerEndpoints(_composer, facade, config: null);
        await app.StartAsync(Ct);
        return new StubHost(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync(Ct);
            await _app.DisposeAsync();
        }

        await _composer.DisposeAsync();
        _dbContext.Dispose();
    }

    /// <summary>A throwaway host plus the client that addresses it; disposes both.</summary>
    private sealed class StubHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    /// <summary>A facade stub answering every operation with one prescribed kind/message (or a success).</summary>
    private sealed class StubFacade : IComposerFacade
    {
        public FacadeErrorKind Kind { get; init; } = FacadeErrorKind.None;
        public string? Error { get; init; }

        public Task<FacadeResult<CurrentModelDto>> GetCurrentModelAsync(ClaimsPrincipal? actingUser, CancellationToken ct) =>
            Task.FromResult(Answer(new CurrentModelDto("stub-model"), value => new FacadeResult<CurrentModelDto>(true, value, null, FacadeErrorKind.None)));

        public Task<FacadeResult<ComposerModelsDto>> GetModelsAsync(ClaimsPrincipal? actingUser, CancellationToken ct) =>
            Task.FromResult(Answer(new ComposerModelsDto([], null), value => new FacadeResult<ComposerModelsDto>(true, value, null, FacadeErrorKind.None)));

        public Task<FacadeResult<SwitchResultDto>> SwitchModelAsync(
            ClaimsPrincipal? actingUser, string? model, string? reasoning, CancellationToken ct) =>
            Task.FromResult(Answer(new SwitchResultDto(model ?? "stub-model", null), value => new FacadeResult<SwitchResultDto>(true, value, null, FacadeErrorKind.None)));

        public Task<FacadeResult<CompactResultDto>> CompactAsync(ClaimsPrincipal? actingUser, CancellationToken ct) =>
            Task.FromResult(Answer(new CompactResultDto(false, 0), value => new FacadeResult<CompactResultDto>(true, value, null, FacadeErrorKind.None)));

        public Task<FacadeResult<CompactResultDto>> CompactPartialAsync(
            ClaimsPrincipal? actingUser, int percent, CancellationToken ct) =>
            Task.FromResult(Answer(new CompactResultDto(false, 0), value => new FacadeResult<CompactResultDto>(true, value, null, FacadeErrorKind.None)));

        /// <summary>The prescribed failure, or the success value through <paramref name="succeeded"/>.</summary>
        private FacadeResult<T> Answer<T>(T value, Func<T, FacadeResult<T>> succeeded) =>
            Kind == FacadeErrorKind.None ? succeeded(value) : new(false, default, Error, Kind);
    }

    /// <summary>A scoped facade whose payload identifies it, proving WHICH instance served the request.</summary>
    private sealed class ScopedMarkerFacade : IComposerFacade
    {
        public const string ModelName = "served-by-the-request-scoped-facade";

        public Task<FacadeResult<CurrentModelDto>> GetCurrentModelAsync(ClaimsPrincipal? actingUser, CancellationToken ct) =>
            Task.FromResult(new FacadeResult<CurrentModelDto>(true, new CurrentModelDto(ModelName), null, FacadeErrorKind.None));

        public Task<FacadeResult<ComposerModelsDto>> GetModelsAsync(ClaimsPrincipal? actingUser, CancellationToken ct) =>
            Task.FromResult(new FacadeResult<ComposerModelsDto>(true, new ComposerModelsDto([], null), null, FacadeErrorKind.None));

        public Task<FacadeResult<SwitchResultDto>> SwitchModelAsync(
            ClaimsPrincipal? actingUser, string? model, string? reasoning, CancellationToken ct) =>
            Task.FromResult(new FacadeResult<SwitchResultDto>(true, new SwitchResultDto(model ?? ModelName, null), null, FacadeErrorKind.None));

        public Task<FacadeResult<CompactResultDto>> CompactAsync(ClaimsPrincipal? actingUser, CancellationToken ct) =>
            Task.FromResult(new FacadeResult<CompactResultDto>(true, new CompactResultDto(false, 0), null, FacadeErrorKind.None));

        public Task<FacadeResult<CompactResultDto>> CompactPartialAsync(
            ClaimsPrincipal? actingUser, int percent, CancellationToken ct) =>
            Task.FromResult(new FacadeResult<CompactResultDto>(true, new CompactResultDto(false, 0), null, FacadeErrorKind.None));
    }
}
