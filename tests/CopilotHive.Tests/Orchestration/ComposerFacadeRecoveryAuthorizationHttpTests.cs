using System.Net;
using System.Security.Claims;
using System.Text.Json;

using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;
using CopilotHive.Services;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// THE ENDPOINT-LEVEL AUTHORIZATION MAPPINGS over the REAL host after the per-request facade
/// resolution: an unauthenticated API call is still a bare 401 with NO redirect, and a Viewer is
/// still a 403 with the sanitized body — neither is remapped into the new 503/409 recovery
/// answers.
/// <para>
/// The host is the PRODUCTION <c>Program.cs</c> pipeline (authentication, the fallback authorization
/// policy and the path-aware challenge handler), so the 401 really is the host's own
/// authorization answer and not a facade-produced one.
/// </para>
/// </summary>
[Collection("EnvVarMutation")]
public sealed class ComposerFacadeRecoveryAuthorizationHttpTests : IDisposable
{
    private const string ViewerId = "recovery-http-viewer";
    private const string MemberId = "recovery-http-member";

    /// <summary>
    /// A startup filter that signs a caller in through the PRODUCTION cookie scheme — the
    /// test-only stand-in for the GitHub OAuth callback.
    /// </summary>
    private sealed class SignInFilter : IStartupFilter
    {
        internal const string SignInPath = "/__test/signin";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Path.StartsWithSegments(SignInPath))
                {
                    var gitHubId = context.Request.Path.Value![SignInPath.Length..].Trim('/');
                    var identity = new ClaimsIdentity(
                        [
                            new Claim(ClaimTypes.NameIdentifier, gitHubId),
                            // A FORGED role claim: the facade must never trust it.
                            new Claim(ClaimTypes.Role, "Admin"),
                        ],
                        authenticationType: CookieAuthenticationDefaults.AuthenticationScheme);

                    await context.SignInAsync(
                        CookieAuthenticationDefaults.AuthenticationScheme,
                        new ClaimsPrincipal(identity));

                    context.Response.StatusCode = StatusCodes.Status200OK;
                    await context.Response.WriteAsync("signed-in");
                    return;
                }

                await nextMiddleware();
            });

            next(app);
        };
    }

    private sealed class AuthEnabledFactory : WebApplicationFactory<Program>
    {
        private readonly string _stateDir =
            Path.Combine(Path.GetTempPath(), $"copilothive-recovery-http-{Guid.NewGuid():N}");
        private readonly string? _previousStateDir;
        private readonly string? _previousClientId;
        private readonly string? _previousClientSecret;
        private readonly string? _previousAllowInsecure;

        internal AuthEnabledFactory()
        {
            _previousStateDir = Environment.GetEnvironmentVariable("STATE_DIR");
            _previousClientId = Environment.GetEnvironmentVariable("GITHUB_OAUTH_CLIENT_ID");
            _previousClientSecret = Environment.GetEnvironmentVariable("GITHUB_OAUTH_CLIENT_SECRET");
            _previousAllowInsecure = Environment.GetEnvironmentVariable("ALLOW_INSECURE_OAUTH");

            Environment.SetEnvironmentVariable("STATE_DIR", _stateDir);
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_ID", "test-client-id");
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_SECRET", "test-secret");
            Environment.SetEnvironmentVariable("ALLOW_INSECURE_OAUTH", "true");
            Directory.CreateDirectory(_stateDir);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                services.ReplaceDistributedBrain(new NoOpDistributedBrain());
                services.AddSingleton<IStartupFilter, SignInFilter>();
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            Environment.SetEnvironmentVariable("STATE_DIR", _previousStateDir);
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_ID", _previousClientId);
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_SECRET", _previousClientSecret);
            Environment.SetEnvironmentVariable("ALLOW_INSECURE_OAUTH", _previousAllowInsecure);

            if (!disposing || !Directory.Exists(_stateDir))
                return;

            try { Directory.Delete(_stateDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private readonly AuthEnabledFactory _factory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task SeedAsync(string gitHubId, UserRole role)
    {
        var factory = _factory.Services.GetRequiredService<IDbContextFactory<CopilotHiveDbContext>>();
        await using var db = await factory.CreateDbContextAsync(Ct);

        db.Users.Add(new UserEntity
        {
            GitHubId = gitHubId,
            Username = gitHubId,
            AccessToken = $"token-{gitHubId}",
            Role = role,
            Enabled = true,
            CreatedAt = "2024-01-01T00:00:00.0000000Z",
        });

        await db.SaveChangesAsync(Ct);
    }

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

    private async Task<HttpClient> SignInAsync(string gitHubId)
    {
        var client = CreateClient();
        var response = await client.GetAsync($"{SignInFilter.SignInPath}/{gitHubId}", Ct);
        response.EnsureSuccessStatusCode();
        return client;
    }

    // ── The 401 ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// EVERY UNAUTHENTICATED API CALL IS STILL A BARE 401 WITH NO REDIRECT: the host's
    /// authorization layer answers before the endpoint runs, so the per-request facade is never
    /// resolved and the answer is never one of the new recovery answers.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/composer/current-model")]
    [InlineData("GET", "/api/composer/models")]
    [InlineData("POST", "/api/composer/models/switch?model=claude-opus&reasoning=medium")]
    [InlineData("POST", "/api/composer/compact")]
    [InlineData("POST", "/api/composer/compact-partial?percent=50")]
    public async Task UnauthenticatedApiCalls_AreStill401_WithNoRedirect(string method, string path)
    {
        using var client = CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        // Never remapped into the recovery answers.
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ── The 403 ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A VIEWER IS STILL 403 WITH THE SANITIZED BODY on every Composer endpoint — the facade's own
    /// authorization answer, never a 503 or a 409 and never the 400 of the compaction catch-all.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/composer/current-model")]
    [InlineData("GET", "/api/composer/models")]
    [InlineData("POST", "/api/composer/models/switch?model=claude-opus&reasoning=medium")]
    [InlineData("POST", "/api/composer/compact")]
    [InlineData("POST", "/api/composer/compact-partial?percent=50")]
    public async Task ViewerComposerCalls_AreStill403_WithTheSanitizedBody(string method, string path)
    {
        await SeedAsync(ViewerId, UserRole.Viewer);
        using var client = await SignInAsync(ViewerId);

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Conflict, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(
            "The acting account is not authorized to perform this operation.",
            doc.RootElement.GetProperty("error").GetString());
    }

    /// <summary>
    /// AN AUTHENTICATED MEMBER'S READ RUNS ON THEIR OWN PER-USER RUNTIME — FOR EVERY VALUE OF THE RETIRED
    /// GATE VARIABLE: the read succeeds with the real payload, and the per-user runtime manager now really
    /// owns a runtime for that account. In particular the <c>"false"</c> row proves the variable can no
    /// longer keep an authenticated installation on the legacy singleton (the split runtime this goal
    /// closes), and the unset/absent row proves the integrated path is the DEFAULT one.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("true")]
    public async Task AnAuthenticatedMemberRead_RunsOnTheirOwnPerUserRuntime_ForEveryRetiredVariableValue(
        string? retiredVariableValue)
    {
        var previous = Environment.GetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable);
        try
        {
            // Set BEFORE the first request so a host that (wrongly) read the variable would observe it.
            Environment.SetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable, retiredVariableValue);

            await SeedAsync(MemberId, UserRole.Member);
            using var client = await SignInAsync(MemberId);

            var response = await client.GetAsync("/api/composer/models", Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // The per-user runtime really exists for THIS account: the integrated path is live.
            var manager = _factory.Services.GetRequiredService<ComposerRuntimeManager>();
            var entriesField = typeof(ComposerRuntimeManager)
                .GetField("_entries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var entries = (System.Collections.IDictionary)entriesField.GetValue(manager)!;

            var users = _factory.Services.GetRequiredService<IDbContextFactory<CopilotHiveDbContext>>();
            await using var db = await users.CreateDbContextAsync(Ct);
            var memberInternalId = (await db.Users.SingleAsync(u => u.GitHubId == MemberId, Ct)).Id;

            Assert.Contains(memberInternalId, entries.Keys.Cast<int>());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable, previous);
        }
    }

    public void Dispose() => _factory.Dispose();
}
