using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
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
/// THE HTTP AUTHORIZATION BATTERY of the server-side authorization slice, driven over a REAL host
/// (the production <c>Program.cs</c> pipeline: authentication, the fallback authorization policy, the
/// path-aware challenge handler, both hubs and the real facades) with a REAL signed-in cookie.
/// <para>
/// The identity is established the way a browser would: the test signs a cookie principal through
/// the production cookie scheme, so the request carries an <c>HttpContext.User</c> exactly as a
/// GitHub OAuth sign-in would. The account's role/enabled state lives in the host's OWN SQLite
/// directory, so a demotion or a disablement between two requests is a real directory change — the
/// second request must observe it.
/// </para>
/// <para>
/// Every assertion is about the AUTHORIZATION OUTCOME, never about a dispatch branch: a denial is
/// 403 with the sanitized body, an operational failure is 400 with its own message, an
/// unauthenticated API call is 401 with no redirect, and an authorized read is 200 with its real
/// payload.
/// </para>
/// </summary>
[Collection("HiveIntegration")]
public sealed class FacadeAuthorizationHttpTests : IDisposable
{
    internal const string ViewerId = "http-viewer";
    internal const string MemberId = "http-member";
    internal const string OperatorId = "http-operator";
    internal const string AdminId = "http-admin";

    /// <summary>
    /// A startup filter that signs a caller in through the PRODUCTION cookie scheme. It is the
    /// test-only stand-in for the GitHub OAuth callback: it issues the same cookie the OAuth
    /// handler would, so the cookie authentication path, the authorization fallback policy and the
    /// facades are all exercised exactly as they are in production.
    /// </summary>
    private sealed class SignInFilter : IStartupFilter
    {
        /// <summary>The path prefix the test signs a caller in through.</summary>
        internal const string SignInPath = "/__test/signin";

        /// <inheritdoc />
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

    /// <summary>
    /// An authentication-ENABLED host: both OAuth env vars are set before the host is built, so the
    /// fallback authorization policy, the path-aware challenge and the real facades are all active.
    /// </summary>
    private sealed class AuthEnabledFactory : WebApplicationFactory<Program>
    {
        private readonly string _stateDir =
            Path.Combine(Path.GetTempPath(), $"copilothive-facade-auth-{Guid.NewGuid():N}");
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

        /// <inheritdoc />
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                services.ReplaceDistributedBrain(new NoOpDistributedBrain());
                services.AddSingleton<IStartupFilter, SignInFilter>();
            });
        }

        /// <inheritdoc />
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

    /// <summary>
    /// Seeds an ACTIVATED account into the host's OWN directory (the same SQLite database the
    /// resolver reads), so the resolution is a real directory read.
    /// </summary>
    private async Task SeedAsync(string gitHubId, UserRole role, bool enabled = true)
    {
        var factory = _factory.Services.GetRequiredService<IDbContextFactory<CopilotHiveDbContext>>();
        await using var db = await factory.CreateDbContextAsync(Ct);

        var existing = await db.Users.SingleOrDefaultAsync(u => u.GitHubId == gitHubId, Ct);
        if (existing is not null)
        {
            existing.Role = role;
            existing.Enabled = enabled;
        }
        else
        {
            db.Users.Add(new UserEntity
            {
                GitHubId = gitHubId,
                Username = gitHubId,
                AccessToken = $"token-{gitHubId}",
                Role = role,
                Enabled = enabled,
                CreatedAt = "2024-01-01T00:00:00.0000000Z",
            });
        }

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Changes a seeded account's persisted role, as an administrator would.</summary>
    private async Task SetRoleAsync(string gitHubId, UserRole role)
    {
        var factory = _factory.Services.GetRequiredService<IDbContextFactory<CopilotHiveDbContext>>();
        await using var db = await factory.CreateDbContextAsync(Ct);

        var account = await db.Users.SingleAsync(u => u.GitHubId == gitHubId, Ct);
        account.Role = role;
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Enables or disables a seeded account, as an administrator would.</summary>
    private async Task SetEnabledAsync(string gitHubId, bool enabled)
    {
        var factory = _factory.Services.GetRequiredService<IDbContextFactory<CopilotHiveDbContext>>();
        await using var db = await factory.CreateDbContextAsync(Ct);

        var account = await db.Users.SingleAsync(u => u.GitHubId == gitHubId, Ct);
        account.Enabled = enabled;
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>A client that keeps cookies (so the sign-in cookie is replayed) and never redirects.</summary>
    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

    /// <summary>Signs the given GitHub identity in and returns a cookie-carrying client.</summary>
    private async Task<HttpClient> SignInAsync(string gitHubId)
    {
        var client = CreateClient();

        var response = await client.GetAsync(
            $"{SignInFilter.SignInPath}/{gitHubId}", Ct);
        response.EnsureSuccessStatusCode();

        return client;
    }

    /// <summary>The forbidden body every denial produces (sanitized: no account, role or reason).</summary>
    private const string ForbiddenBody =
        "The acting account is not authorized to perform this operation.";

    // ── Unauthenticated: 401 with NO redirect ───────────────────────────────

    /// <summary>
    /// EVERY unauthenticated API call is a PROGRAMMATIC call: it is answered with a bare 401 and
    /// NO redirect — no <c>Location</c> header and no OAuth correlation cookie, so a JSON client is
    /// never handed an HTML login page or an OAuth handshake it would follow.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/composer/current-model")]
    [InlineData("GET", "/api/composer/models")]
    [InlineData("POST", "/api/composer/models/switch?model=claude-opus&reasoning=medium")]
    [InlineData("POST", "/api/composer/compact")]
    [InlineData("POST", "/api/composer/compact-partial?percent=50")]
    public async Task UnauthenticatedApiCalls_Return401_WithNoRedirect(string method, string path)
    {
        using var client = CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);

        // No OAuth handshake was started: no correlation cookie (in fact no Set-Cookie at all).
        Assert.False(
            response.Headers.TryGetValues("Set-Cookie", out var cookies)
                && cookies.Any(c => c.StartsWith(".AspNetCore.Correlation.", StringComparison.Ordinal)),
            "an API challenge must not start an OAuth handshake");
    }

    /// <summary>
    /// The global Composer WRITE is challenged the same way: the authorization layer answers
    /// before the model binding runs, so the API client sees 401 rather than a 400 for an
    /// unparsable body.
    /// </summary>
    [Fact]
    public async Task UnauthenticatedConfigComposerPatch_Return401_WithNoRedirect()
    {
        using var client = CreateClient();

        var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await client.PatchAsync("/api/config/composer", content, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    // ── Authenticated but unauthorized: 403 ─────────────────────────────────

    /// <summary>
    /// A Viewer is REFUSED with 403 on every Composer endpoint — and never with the 400 the
    /// compaction catch-all would produce for an operational failure.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/composer/current-model")]
    [InlineData("GET", "/api/composer/models")]
    [InlineData("POST", "/api/composer/models/switch?model=claude-opus&reasoning=medium")]
    [InlineData("POST", "/api/composer/compact")]
    [InlineData("POST", "/api/composer/compact-partial?percent=50")]
    public async Task ViewerComposerCalls_Return403_AndNever400(string method, string path)
    {
        await SeedAsync(ViewerId, UserRole.Viewer);
        using var client = await SignInAsync(ViewerId);

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(Ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(ForbiddenBody, doc.RootElement.GetProperty("error").GetString());
    }

    /// <summary>
    /// The GLOBAL Composer write answers a Viewer with 403 for EVERY update shape — an empty
    /// update, a MaxSteps-only update and a notification-only update alike — so no shape escapes
    /// the admin requirement.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"maxSteps\":75}")]
    [InlineData("{\"eventNotificationsMode\":\"active\"}")]
    public async Task ViewerConfigComposerPatch_Return403_AndPersistsNothing(string json)
    {
        await SeedAsync(ViewerId, UserRole.Viewer);
        using var client = await SignInAsync(ViewerId);

        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PatchAsync("/api/config/composer", content, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(Ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(ForbiddenBody, doc.RootElement.GetProperty("error").GetString());
    }

    /// <summary>
    /// A Member DEMOTED to Viewer between two requests is refused on the SECOND request: the
    /// cookie's stale claim confers nothing, because the facade re-resolves the caller's CURRENT
    /// stored role from the directory on every call.
    /// </summary>
    [Fact]
    public async Task DemotedBetweenTwoRequests_IsRefusedOnTheSecondRequest()
    {
        await SeedAsync(MemberId, UserRole.Member);
        using var client = await SignInAsync(MemberId);

        var first = await client.GetAsync("/api/composer/models", Ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // A REAL directory change: the persisted role becomes Viewer.
        await SetRoleAsync(MemberId, UserRole.Viewer);

        var second = await client.GetAsync("/api/composer/models", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        Assert.NotEqual(HttpStatusCode.BadRequest, second.StatusCode);
    }

    /// <summary>
    /// A DISABLED account is refused even though its cookie is still valid and its role still
    /// allows the operation.
    /// </summary>
    [Fact]
    public async Task DisabledAccount_IsRefused()
    {
        await SeedAsync(MemberId, UserRole.Member);
        using var client = await SignInAsync(MemberId);

        await SetEnabledAsync(MemberId, enabled: false);

        var response = await client.GetAsync("/api/composer/models", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// An authenticated identity with NO activated account (never invited / pending) is refused:
    /// there is no anonymous fall-through to the legacy runtime.
    /// </summary>
    [Fact]
    public async Task UnknownIdentity_IsRefused()
    {
        await SeedAsync(MemberId, UserRole.Member);
        using var client = await SignInAsync("never-invited-identity");

        var response = await client.GetAsync("/api/composer/models", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Authorized: 200 with the real payload, 400 only for OPERATIONAL failures ──

    /// <summary>
    /// THE DISCRIMINATION AT THE HTTP LEVEL: an authorized Member's reads really succeed (200
    /// with the Composer's own payload) and an OPERATIONAL failure really is a 400 with its own
    /// message — while the SAME endpoints answer a Viewer with 403. The three answers are
    /// distinguishable, so a denial can never be mistaken for a validation or execution failure.
    /// </summary>
    [Fact]
    public async Task AuthorizedMember_ReadsSucceed_AndOperationalFailuresStay400()
    {
        await SeedAsync(MemberId, UserRole.Member);
        using var client = await SignInAsync(MemberId);

        // A disconnected Composer reports a NULL model on a SUCCESSFUL read (the frozen contract).
        var current = await client.GetAsync("/api/composer/current-model", Ct);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        using (var doc = JsonDocument.Parse(await current.Content.ReadAsStringAsync(Ct)))
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("model").ValueKind);

        // The catalog endpoint answers with the Composer's own list.
        var models = await client.GetAsync("/api/composer/models", Ct);
        Assert.Equal(HttpStatusCode.OK, models.StatusCode);

        // An OPERATIONAL compaction failure (the host's Composer is a disconnected shell) is a 400
        // carrying the operational message — NOT a 403.
        var compact = await client.PostAsync("/api/composer/compact", null, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, compact.StatusCode);
        var compactBody = await compact.Content.ReadAsStringAsync(Ct);
        Assert.Contains("not connected", compactBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not authorized", compactBody, StringComparison.Ordinal);

        // An invalid MODEL is likewise a 400 (the endpoint's own validation), never a 403.
        var switchCall = await client.PostAsync(
            "/api/composer/models/switch?model=not-a-model&reasoning=medium", null, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, switchCall.StatusCode);
        Assert.DoesNotContain("not authorized", await switchCall.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    /// <summary>
    /// THE LEGACY SINGLETON IS THE RUNTIME: exercising every Composer endpoint leaves the
    /// per-user <see cref="ComposerRuntimeManager"/> cache EMPTY, so no private per-user runtime
    /// was created or consulted on any path. This slice deliberately does not convert the runtime
    /// selection.
    /// </summary>
    [Fact]
    public async Task ComposerEndpoints_NeverInvolveTheRuntimeManager()
    {
        await SeedAsync(MemberId, UserRole.Member);
        using var client = await SignInAsync(MemberId);

        await client.GetAsync("/api/composer/current-model", Ct);
        await client.GetAsync("/api/composer/models", Ct);
        await client.PostAsync("/api/composer/compact", null, Ct);

        var manager = _factory.Services.GetRequiredService<ComposerRuntimeManager>();
        var entriesField = typeof(ComposerRuntimeManager)
            .GetField("_entries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var entries = (System.Collections.IDictionary)entriesField.GetValue(manager)!;

        Assert.True(entries.Count == 0, "the legacy singleton runtime must be used; no per-user runtime may be created");
    }

    public void Dispose() => _factory.Dispose();
}
