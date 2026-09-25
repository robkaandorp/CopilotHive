using System.Net;
using System.Security.Claims;
using System.Text.Json;

using AspNet.Security.OAuth.GitHub;

using CopilotHive.Git;
using CopilotHive.Services;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
        private readonly string? _previousAllowInsecureOAuth;

        public AuthEnabledFactory()
        {
            _previousStateDir = Environment.GetEnvironmentVariable("STATE_DIR");
            _previousOAuthClientId = Environment.GetEnvironmentVariable("GITHUB_OAUTH_CLIENT_ID");
            _previousOAuthClientSecret = Environment.GetEnvironmentVariable("GITHUB_OAUTH_CLIENT_SECRET");
            _previousAllowInsecureOAuth = Environment.GetEnvironmentVariable("ALLOW_INSECURE_OAUTH");
            Environment.SetEnvironmentVariable("STATE_DIR", _stateDir);
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_ID", "test-client-id");
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_SECRET", "test-secret");
            Environment.SetEnvironmentVariable("ALLOW_INSECURE_OAUTH", "true");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            // The Brain is mandatory in production (Program registers it unconditionally and
            // resolves it eagerly after builder.Build()); this host supplies its own explicitly.
            builder.ConfigureServices(services =>
                services.ReplaceDistributedBrain(new NoOpDistributedBrain()));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            Environment.SetEnvironmentVariable("STATE_DIR", _previousStateDir);
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_ID", _previousOAuthClientId);
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_SECRET", _previousOAuthClientSecret);
            Environment.SetEnvironmentVariable("ALLOW_INSECURE_OAUTH", _previousAllowInsecureOAuth);

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

    [Fact]
    public void CorrelationCookie_IsNotSecure_WhenAllowInsecureOAuthIsTrue()
    {
        using var client = _factory.CreateClient();

        var options = _factory.Services.GetRequiredService<IOptionsMonitor<GitHubAuthenticationOptions>>()
            .Get("GitHub");

        Assert.Equal(CookieSecurePolicy.None, options.CorrelationCookie.SecurePolicy);
        Assert.Equal(SameSiteMode.Lax, options.CorrelationCookie.SameSite);
    }

    [Fact]
    public async Task CorrelationCookie_Header_HasNoSecureAndSameSiteLax_WhenAllowInsecureOAuthIsTrue()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/api/goals", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var setCookie = Assert.Single(response.Headers.NonValidated["Set-Cookie"]);

        var cookie = setCookie.Split(';', StringSplitOptions.TrimEntries);
        Assert.Contains(cookie, part => part.StartsWith(".AspNetCore.Correlation.", StringComparison.Ordinal));

        // Attributes are the semicolon-delimited parts after the name=value pair; each part is
        // matched as a discrete token so a value merely containing "Secure" (e.g. a cookie
        // value with "Secure" in it) can never satisfy the assertion.
        Assert.DoesNotContain(cookie, part => string.Equals(part, "Secure", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(cookie, part => string.Equals(part, "SameSite=Lax", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GitHubOptions_Scope_ContainsRepoAlongsideTheOtherConfiguredScopes()
    {
        using var client = _factory.CreateClient();

        var options = _factory.Services.GetRequiredService<IOptionsMonitor<GitHubAuthenticationOptions>>()
            .Get("GitHub");

        // The 'repo' scope is what lets the stored token clone/push PRIVATE config repos that
        // are provisioned to workers via GetWorkerConfig.config_repo_url.
        Assert.Contains("repo", options.Scope);
        Assert.Contains("read:user", options.Scope);
        Assert.Contains("copilot", options.Scope);
        Assert.Contains("workflow", options.Scope);
    }

    // ── avatar claim-action mapping ───────────────────────────────────────────
    // Program.cs must map GitHub's "avatar_url" JSON key onto the GitHubClaimTypes.Avatar claim
    // type, because AspNet.Security.OAuth.GitHub maps only id/login/email/name/url on its own.
    // Without the MapJsonKey action the claim is never emitted, so both the stored User.AvatarUrl
    // and the nav-bar <img> stay empty.

    private const string AvatarJsonKey = "avatar_url";

    [Fact]
    public void GitHubOptions_ClaimActions_MapAvatarJsonKeyOntoAvatarClaim()
    {
        using var client = _factory.CreateClient();

        var options = _factory.Services.GetRequiredService<IOptionsMonitor<GitHubAuthenticationOptions>>()
            .Get("GitHub");

        var avatarAction = Assert.Single(options.ClaimActions, action =>
            action is JsonKeyClaimAction jsonKey
            && jsonKey.ClaimType == GitHubClaimTypes.Avatar
            && jsonKey.JsonKey == AvatarJsonKey);

        Assert.Equal(GitHubClaimTypes.Avatar, avatarAction.ClaimType);
        Assert.Equal(AvatarJsonKey, ((JsonKeyClaimAction)avatarAction).JsonKey);
    }

    [Fact]
    public void GitHubOptions_ClaimActions_ProduceAvatarClaimFromGitHubUserJson()
    {
        using var client = _factory.CreateClient();

        var options = _factory.Services.GetRequiredService<IOptionsMonitor<GitHubAuthenticationOptions>>()
            .Get("GitHub");

        var identity = new ClaimsIdentity(authenticationType: "Test.GitHub");
        using var user = JsonDocument.Parse(
            """{"id":1,"login":"octo","avatar_url":"https://avatars.githubusercontent.com/u/1"}""");

        // This is exactly how the OAuth middleware projects the user-endpoint payload onto the
        // ticket identity: every configured claim action's Run over the JSON element.
        foreach (var action in options.ClaimActions)
        {
            action.Run(user.RootElement, identity, issuer: "GitHub");
        }

        var avatarClaim = Assert.Single(identity.FindAll(GitHubClaimTypes.Avatar));
        Assert.Equal("https://avatars.githubusercontent.com/u/1", avatarClaim.Value);
    }

    public void Dispose() => _factory.Dispose();
}
