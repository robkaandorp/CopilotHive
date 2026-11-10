using System.Net;
using System.Text;
using System.Text.Json;

using CopilotHive.Goals;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// THE OPEN-MODE HTTP BEHAVIOUR IS UNCHANGED: with no OAuth configured, the host runs in open
/// mode, the resolver yields the legacy local operator, and the endpoints behave EXACTLY as they
/// did before the authorization slice.
/// <para>
/// The control rows are deliberately the pre-slice answers: an unauthenticated caller is NOT
/// challenged (open mode has no fallback policy), the Composer reads answer 200 with their real
/// payloads, and the global Composer write reaches its own service answer (a 500 problem-details
/// body when no config repo is registered) rather than a 403.
/// </para>
/// <para>
/// This is the class that would fail if the authorization gate were applied unconditionally —
/// i.e. if the facade ignored the host's explicit auth mode and treated every installation as
/// authenticated-only.
/// </para>
/// </summary>
[Collection("HiveIntegration")]
public sealed class OpenModeFacadeHttpTests : IDisposable
{
    /// <summary>
    /// The shared OPEN-MODE host: no OAuth env vars, so the resolver yields the legacy local
    /// operator for every caller and the authorization gate admits it.
    /// </summary>
    private sealed class OpenModeFactory : WebApplicationFactory<Program>
    {
        private readonly string _stateDir =
            Path.Combine(Path.GetTempPath(), $"copilothive-open-mode-{Guid.NewGuid():N}");
        private readonly string? _previousStateDir;
        private readonly string? _previousClientId;
        private readonly string? _previousClientSecret;

        internal OpenModeFactory()
        {
            _previousStateDir = Environment.GetEnvironmentVariable("STATE_DIR");
            _previousClientId = Environment.GetEnvironmentVariable("GITHUB_OAUTH_CLIENT_ID");
            _previousClientSecret = Environment.GetEnvironmentVariable("GITHUB_OAUTH_CLIENT_SECRET");

            Environment.SetEnvironmentVariable("STATE_DIR", _stateDir);
            // Open mode: BOTH OAuth vars unset — the explicit host decision the resolver reads.
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_ID", null);
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_SECRET", null);
            Directory.CreateDirectory(_stateDir);
        }

        /// <inheritdoc />
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
                services.ReplaceDistributedBrain(new NoOpDistributedBrain()));
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            Environment.SetEnvironmentVariable("STATE_DIR", _previousStateDir);
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_ID", _previousClientId);
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_SECRET", _previousClientSecret);

            if (!disposing || !Directory.Exists(_stateDir))
                return;

            try { Directory.Delete(_stateDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private readonly OpenModeFactory _factory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// THE HOST REALLY IS IN OPEN MODE: the registered auth mode reports authentication disabled,
    /// and the resolver answers an anonymous caller with the legacy local operator. Without this
    /// control the rows below could be satisfied by an authentication-enabled host that happened
    /// to answer the same way.
    /// </summary>
    [Fact]
    public async Task TheHost_IsInOpenMode_AndTheResolverYieldsTheLocalOperator()
    {
        using var client = _factory.CreateClient();

        var mode = _factory.Services.GetRequiredService<CopilotHive.Security.HiveAuthMode>();
        Assert.False(mode.AuthEnabled, "the host must really be in open mode");

        var resolver = _factory.Services.GetRequiredService<CopilotHive.Security.HivePrincipalResolver>();
        var resolution = await resolver.ResolveAsync(null, Ct);

        Assert.True(resolution.IsResolved);
        var principal = Assert.IsType<CopilotHive.Security.ActingUserPrincipal>(resolution.Principal);
        Assert.True(principal.IsLocalOperator);
    }

    /// <summary>
    /// AN UNAUTHENTICATED CALLER IS NOT CHALLENGED IN OPEN MODE: the endpoint answers with its
    /// own result (no 401, no redirect) exactly as it did before the slice.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/composer/current-model")]
    [InlineData("GET", "/api/composer/models")]
    public async Task OpenMode_UnauthenticatedApiCalls_AreNotChallenged(string method, string path)
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await client.SendAsync(request, Ct);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    /// <summary>
    /// THE COMPOSER READS BEHAVE EXACTLY AS BEFORE: the current-model read answers 200 with a NULL
    /// model (the frozen contract for a disconnected Composer) and the catalog answers 200 with the
    /// Composer's own list.
    /// </summary>
    [Fact]
    public async Task OpenMode_ComposerReads_KeepTheirPreSliceAnswers()
    {
        using var client = _factory.CreateClient();

        var current = await client.GetAsync("/api/composer/current-model", Ct);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        using (var doc = JsonDocument.Parse(await current.Content.ReadAsStringAsync(Ct)))
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("model").ValueKind);

        var models = await client.GetAsync("/api/composer/models", Ct);
        Assert.Equal(HttpStatusCode.OK, models.StatusCode);
    }

    /// <summary>
    /// THE GLOBAL COMPOSER WRITE IS NOT ADMIN-GATED IN OPEN MODE: it reaches its own service
    /// answer (a 500 problem-details body, because the shared test host registers no config
    /// repo) instead of a 403. The local operator remains the installation's administrator.
    /// </summary>
    [Fact]
    public async Task OpenMode_ConfigComposerPatch_ReachesItsServiceAnswer_NotA403()
    {
        using var client = _factory.CreateClient();

        var content = new StringContent("{\"maxSteps\":50}", Encoding.UTF8, "application/json");
        var response = await client.PatchAsync("/api/config/composer", content, Ct);

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// THE COMPOSER COMPACTION ROUTE IS UNCHANGED IN OPEN MODE: an operational failure is still a
    /// 400 carrying the operational message — the authorization slice introduced no new answer
    /// for the single-operator installation.
    /// </summary>
    [Fact]
    public async Task OpenMode_ComposerCompact_KeepsItsOperationalBadRequest()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/composer/compact", null, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not connected", await response.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _factory.Dispose();
}
