using System.Net;

using CopilotHive.Git;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests verifying authentication is actually ENFORCED when GitHub OAuth is
/// enabled (both <c>GITHUB_OAUTH_CLIENT_ID</c> and <c>GITHUB_OAUTH_CLIENT_SECRET</c> set).
/// Unlike <see cref="AuthenticationTests"/> (open mode), these boot a dedicated factory that
/// sets the OAuth env vars so the fallback authorization policy is active. Protected endpoints
/// must reject unauthenticated callers, while explicitly-anonymous endpoints (health, login)
/// stay reachable.
/// </summary>
[Collection("HiveIntegration")]
public sealed class AuthenticatedModeTests : IDisposable
{
    /// <summary>
    /// A <see cref="WebApplicationFactory{TEntryPoint}"/> that enables GitHub OAuth by setting
    /// the OAuth client id/secret env vars (and an isolated <c>STATE_DIR</c>) before the host is
    /// built. Not shared via a collection fixture — each test class instance owns its own factory
    /// so the env-var mutation does not leak into the shared open-mode integration tests.
    /// </summary>
    private sealed class AuthEnabledFactory : WebApplicationFactory<Program>
    {
        private readonly string _stateDir =
            Path.Combine(Path.GetTempPath(), $"copilothive-authtest-{Guid.NewGuid():N}");
        private readonly string? _previousStateDir;
        private readonly string? _previousOAuthClientId;
        private readonly string? _previousOAuthClientSecret;

        public AuthEnabledFactory()
        {
            _previousStateDir = Environment.GetEnvironmentVariable("STATE_DIR");
            _previousOAuthClientId = Environment.GetEnvironmentVariable("GITHUB_OAUTH_CLIENT_ID");
            _previousOAuthClientSecret = Environment.GetEnvironmentVariable("GITHUB_OAUTH_CLIENT_SECRET");
            Environment.SetEnvironmentVariable("STATE_DIR", _stateDir);
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_ID", "test-client-id");
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_SECRET", "test-secret");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            Environment.SetEnvironmentVariable("STATE_DIR", _previousStateDir);
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_ID", _previousOAuthClientId);
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_SECRET", _previousOAuthClientSecret);

            if (!disposing || !Directory.Exists(_stateDir))
                return;

            try
            {
                Directory.Delete(_stateDir, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private readonly AuthEnabledFactory _factory = new();

    [Fact]
    public async Task ProtectedEndpoint_ReturnsNon200_WhenUnauthenticated()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/api/goals", TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden
                or HttpStatusCode.Found
                or HttpStatusCode.Redirect
                or HttpStatusCode.SeeOther,
            $"Expected an auth-rejection status (401/403/302), got {(int)response.StatusCode} {response.StatusCode}.");
    }

    [Fact]
    public async Task HealthEndpoint_Returns200_WithAuthEnabled()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LoginEndpoint_Returns200_WithAuthEnabled()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/login", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task StaticFileEndpoint_Returns200_WithoutAuth_WithAuthEnabled()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/css/site.css", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual("/login", response.Headers.Location?.OriginalString);
    }

    public void Dispose() => _factory.Dispose();
}
