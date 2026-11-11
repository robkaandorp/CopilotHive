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
/// THE ADDITIVE RECOVERY MAPPINGS AT THE HTTP LEVEL: <see cref="FacadeErrorKind.ServiceUnavailable"/> is a
/// 503 problem-details body and <see cref="FacadeErrorKind.Conflict"/> is a 409 <c>{error}</c> body — and
/// every pre-existing mapping is unchanged.
/// <para>
/// The hub is driven through the REAL endpoint registration. The facade is supplied as the explicit test
/// OVERRIDE for the mapping rows, and the same registration is ALSO driven with <c>null</c> to prove the
/// facade is now resolved PER REQUEST from <c>HttpContext.RequestServices</c>.
/// </para>
/// </summary>
public sealed class ComposerHubRecoveryMappingTests : IAsyncLifetime
{
    private const string DenialMessage = "The acting account is not authorized to perform this operation.";

    private readonly CopilotHiveDbContext _dbContext = CopilotHiveDbContext.CreateInMemory();
    private WebApplication _scopedApp = null!;
    private HttpClient _scopedClient = null!;
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

        // The host registers the facade SCOPED (the production lifetime) and maps the endpoints with NO
        // override, so the handler must take it from the REQUEST's own scope. A startup closure over the
        // root provider could not have captured this instance.
        var builder = WebApplication.CreateBuilder();
        Program.AddHiveJsonOptions(builder.Services);
        builder.Services.AddScoped<IComposerFacade>(_ => new ScopedMarkerFacade());
        _scopedApp = builder.Build();
        _scopedApp.MapComposerEndpoints(_composer, composerFacade: null, config: null);
        await _scopedApp.StartAsync(Ct);
        _scopedClient = new HttpClient { BaseAddress = new Uri(_scopedApp.Urls.First()) };
    }

    // ── Recovery kinds ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="FacadeErrorKind.ServiceUnavailable"/> answers HTTP 503 as a problem-details body carrying the
    /// facade's message — the actionable "still deferred" / "runtime unavailable" answer.
    /// </summary>
    [Fact]
    public async Task ServiceUnavailable_MapsTo503_WithTheFacadeMessage()
    {
        const string message = "The Composer is not connected yet: retry the operation.";
        await using var host = await StubHostAsync(new StubFacade { Kind = FacadeErrorKind.ServiceUnavailable, Error = message });

        var response = await host.Client.PostAsync("/api/composer/models/switch?model=m&reasoning=high", null, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains(message, body, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(503, doc.RootElement.GetProperty("status").GetInt32());
    }

    /// <summary>
    /// <see cref="FacadeErrorKind.Conflict"/> answers HTTP 409 with the <c>{error}</c> body shape — the safe
    /// retry/requery answer for a removed runtime — on EVERY Composer endpoint.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/composer/current-model")]
    [InlineData("GET", "/api/composer/models")]
    [InlineData("POST", "/api/composer/models/switch?model=m&reasoning=high")]
    [InlineData("POST", "/api/composer/compact")]
    [InlineData("POST", "/api/composer/compact-partial?percent=50")]
    public async Task Conflict_MapsTo409_WithTheErrorBody_OnEveryEndpoint(string method, string path)
    {
        const string message = "The Composer runtime was removed; retry the request.";
        await using var host = await StubHostAsync(new StubFacade { Kind = FacadeErrorKind.Conflict, Error = message });

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await host.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(message, doc.RootElement.GetProperty("error").GetString());
    }

    /// <summary>
    /// THE EXISTING MAPPINGS ARE UNCHANGED: 400 and 403 keep their <c>{error}</c> bodies, and an authorization
    /// denial is still never reported as a 400, a 503 or a 409.
    /// </summary>
    [Fact]
    public async Task ExistingMappings_AreUnchanged()
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

    // ── Per-request resolution ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE FACADE IS RESOLVED PER REQUEST: a host that registers it SCOPED and maps the endpoints with NO
    /// override serves the request from its OWN scope's instance — the payload identifies that instance.
    /// </summary>
    [Fact]
    public async Task Facade_IsResolvedPerRequest_FromRequestServices()
    {
        var response = await _scopedClient.GetAsync("/api/composer/current-model", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(ScopedMarkerFacade.ModelName, doc.RootElement.GetProperty("model").GetString());
    }

    /// <summary>
    /// ...AND THE SAME RESOLUTION WORKS FOR EVERY ENDPOINT, so no handler kept a captured instance.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/composer/models")]
    [InlineData("POST", "/api/composer/models/switch?model=m&reasoning=high")]
    [InlineData("POST", "/api/composer/compact")]
    [InlineData("POST", "/api/composer/compact-partial?percent=50")]
    public async Task EveryEndpoint_ResolvesTheScopedFacade(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await _scopedClient.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Boots a throwaway host with the supplied facade as the explicit override. The returned handle disposes
    /// the host, so an assertion failure cannot leak a listening server.
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
        _scopedClient?.Dispose();
        if (_scopedApp is not null)
        {
            await _scopedApp.StopAsync(Ct);
            await _scopedApp.DisposeAsync();
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
