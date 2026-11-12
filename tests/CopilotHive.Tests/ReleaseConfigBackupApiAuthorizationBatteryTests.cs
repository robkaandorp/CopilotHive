using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;
using CopilotHive.Security;
using CopilotHive.Services;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// THE HTTP-LEVEL AUTHORIZATION BATTERY for the release / configuration / backup / clarification /
/// session surfaces, over a REAL host booted with authentication ENABLED, a REAL test
/// authentication scheme and the REAL <see cref="UserService"/> directory (SQLite) the facades
/// re-resolve on every request.
/// <para>
/// THE ANSWERS THIS BATTERY PINS:
/// </para>
/// <list type="bullet">
///   <item>AN ANONYMOUS caller gets 401 with NO redirect and NO <c>Location</c> header on every
///   covered route — the programmatic API challenge, never an OAuth interstitial.</item>
///   <item>An AUTHENTICATED but INSUFFICIENT caller (Viewer/Member/Operator where a higher
///   capability is required), a DEMOTED caller, a DISABLED caller and an UNKNOWN account all get
///   403 with the sanitized <c>{error}</c> body — never 400, never a validation answer, never a
///   login redirect.</item>
///   <item>MALFORMED BODIES from an unauthorized caller are refused with 403 BEFORE binding on
///   every covered MUTATING route that takes a typed body, while an AUTHORIZED caller keeps the
///   existing 400 binding answer.</item>
///   <item>THE EXISTING 409 answers are preserved for an authorized caller (duplicate release
///   creation).</item>
///   <item>THE SUCCESSFUL RESPONSE SHAPES are preserved exactly: the release status envelope, the
///   release DTO, the config DTOs, the backup list/create, the clarification bodies and the
///   session list.</item>
/// </list>
/// <para>
/// EVERY denial row is paired with a VIABLE SUCCESS CONTROL proving the operation really works
/// for a caller who holds the capability — a route that refused everybody would fail the control.
/// </para>
/// </summary>
[Collection("HiveIntegration")]
public sealed class ReleaseConfigBackupApiAuthorizationBatteryTests : IDisposable
{
    /// <summary>The header the test scheme reads the GitHub identity from.</summary>
    private const string IdentityHeader = "X-Test-GitHub-Id";

    private const string ViewerId = "rcb-api-viewer";
    private const string MemberId = "rcb-api-member";
    private const string OperatorId = "rcb-api-operator";
    private const string AdminId = "rcb-api-admin";
    private const string DisabledAdminId = "rcb-api-disabled-admin";
    private const string UnknownId = "rcb-api-unknown-account";

    private const string ForbiddenMessage = "The acting account is not authorized to perform this operation.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The REAL host, booted with OAuth CONFIGURED (so the authentication/authorization pipeline
    /// and the path-aware challenge handler are wired exactly as in production) but with the
    /// default AUTHENTICATION SCHEME replaced by a test scheme that reads the identity from a
    /// request header. The CHALLENGE scheme is left as the production
    /// <see cref="HiveChallengeHandler"/>, so the anonymous 401-no-redirect contract is exercised
    /// against the real handler and not a stand-in.
    /// </summary>
    private sealed class AuthApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _stateDir =
            Path.Combine(Path.GetTempPath(), $"copilothive-rcb-api-{Guid.NewGuid():N}");
        private readonly string? _previousStateDir;
        private readonly string? _previousClientId;
        private readonly string? _previousClientSecret;
        private readonly string? _previousAllowInsecure;

        public AuthApiFactory()
        {
            _previousStateDir = Environment.GetEnvironmentVariable("STATE_DIR");
            _previousClientId = Environment.GetEnvironmentVariable("GITHUB_OAUTH_CLIENT_ID");
            _previousClientSecret = Environment.GetEnvironmentVariable("GITHUB_OAUTH_CLIENT_SECRET");
            _previousAllowInsecure = Environment.GetEnvironmentVariable("ALLOW_INSECURE_OAUTH");

            Environment.SetEnvironmentVariable("STATE_DIR", _stateDir);
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_ID", "test-client-id");
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_SECRET", "test-client-secret");
            Environment.SetEnvironmentVariable("ALLOW_INSECURE_OAUTH", "true");
            Directory.CreateDirectory(_stateDir);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                services.ReplaceDistributedBrain(new NoOpDistributedBrain());

                // The test scheme authenticates the caller from a request header. The CHALLENGE
                // scheme stays the production path-aware handler, so the anonymous 401 contract
                // is exercised against the real implementation.
                services.AddAuthentication(options =>
                {
                    options.DefaultScheme = TestIdentityHandler.SchemeName;
                    options.DefaultChallengeScheme = HiveChallengeHandler.SchemeName;
                }).AddScheme<AuthenticationSchemeOptions, TestIdentityHandler>(
                    TestIdentityHandler.SchemeName, _ => { });
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

    /// <summary>
    /// The test authentication scheme: it authenticates a request that carries the identity
    /// header and refuses every other request (so an anonymous request stays anonymous and the
    /// challenge pipeline answers it).
    /// </summary>
    private sealed class TestIdentityHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string SchemeName = "TestIdentity";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(IdentityHeader, out var values))
                return Task.FromResult(AuthenticateResult.NoResult());

            var gitHubId = values.ToString();
            if (string.IsNullOrWhiteSpace(gitHubId))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, gitHubId)],
                authenticationType: SchemeName);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, SchemeName)));
        }
    }

    private readonly AuthApiFactory _factory = new();

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
    });

    // ── request helpers ─────────────────────────────────────────────────────

    private static HttpRequestMessage Authenticated(HttpMethod method, string url, string gitHubId)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(IdentityHeader, gitHubId);
        return request;
    }

    private static HttpRequestMessage Authenticated(
        HttpMethod method, string url, string gitHubId, HttpContent body)
    {
        var request = Authenticated(method, url, gitHubId);
        request.Content = body;
        return request;
    }

    /// <summary>A FRESH JSON body — never a shared instance, because a request disposes it.</summary>
    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    /// <summary>A FRESH raw body — never a shared instance, because a request disposes it.</summary>
    private static StringContent Raw(string body) =>
        new(body, Encoding.UTF8, "application/json");

    // ── directory seeding (the REAL SQLite directory the facades re-read) ────

    private async Task SeedAccountAsync(string gitHubId, UserRole role, bool enabled = true)
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CopilotHiveDbContext>>();
        await using var db = await factory.CreateDbContextAsync(Ct);
        db.Users.Add(new UserEntity
        {
            GitHubId = gitHubId,
            Username = gitHubId,
            AccessToken = "token-" + gitHubId,
            Role = role,
            Enabled = enabled,
            CreatedAt = "2024-01-01T00:00:00.0000000Z",
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task SetRoleAsync(string gitHubId, UserRole role)
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CopilotHiveDbContext>>();
        await using var db = await factory.CreateDbContextAsync(Ct);
        var entity = await db.Users.SingleAsync(u => u.GitHubId == gitHubId, Ct);
        entity.Role = role;
        await db.SaveChangesAsync(Ct);
    }

    private async Task SetEnabledAsync(string gitHubId, bool enabled)
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CopilotHiveDbContext>>();
        await using var db = await factory.CreateDbContextAsync(Ct);
        var entity = await db.Users.SingleAsync(u => u.GitHubId == gitHubId, Ct);
        entity.Enabled = enabled;
        await db.SaveChangesAsync(Ct);
    }

    private async Task SeedReleaseAsync(string id, string tag)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<CopilotHive.Goals.IGoalStore>();
        await store.CreateReleaseAsync(new CopilotHive.Goals.Release { Id = id, Tag = tag }, Ct);
    }

    private async Task SeedGoalAsync(string id, string releaseId)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<CopilotHive.Goals.IGoalStore>();
        await store.CreateGoalAsync(new CopilotHive.Goals.Goal
        {
            Id = id,
            Description = "Battery goal",
            ReleaseId = releaseId,
        }, Ct);
    }

    /// <summary>
    /// A release id unique to THIS test method. Every test in this class shares ONE host (and so
    /// ONE SQLite database), so a fixed id would collide with the row another test created.
    /// </summary>
    private static string UniqueId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>Enqueues a clarification request into the host's own queue.</summary>
    private void SeedClarification(string id)
    {
        var queue = _factory.Services.GetRequiredService<CopilotHive.Orchestration.ClarificationQueueService>();
        queue.Enqueue(new CopilotHive.Orchestration.ClarificationRequest
        {
            Id = id,
            GoalId = "clar-goal",
            WorkerRole = "coder",
            Question = "Which branch?",
        });
    }

    /// <summary>Registers an execution session and a Composer session in the host's registry.</summary>
    private (string ExecutionId, string ComposerId) SeedSessions()
    {
        var registry = _factory.Services.GetRequiredService<CopilotHive.Dashboard.LlmSessionRegistry>();
        var executionId = $"battery-exec-{Guid.NewGuid():N}";
        var composerId = $"battery-composer-{Guid.NewGuid():N}";

        registry.RegisterOrUpdate(new CopilotHive.Dashboard.LlmSessionInfo
        {
            SessionId = executionId,
            SessionType = CopilotHive.Dashboard.LlmSessionType.Brain,
            Model = "m",
            Status = "active",
            MaxTokens = 1,
        });
        registry.RegisterOrUpdate(new CopilotHive.Dashboard.LlmSessionInfo
        {
            SessionId = composerId,
            SessionType = CopilotHive.Dashboard.LlmSessionType.Composer,
            Model = "m",
            Status = "active",
            MaxTokens = 1,
        });

        return (executionId, composerId);
    }

    // ── shared assertions ───────────────────────────────────────────────────

    /// <summary>Asserts the 403 body carries the sanitized denial text in the <c>{ error }</c> shape.</summary>
    private static async Task AssertForbiddenBodyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        using var doc = JsonDocument.Parse(body);

        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("error", out var error),
            "the 403 body must carry an 'error' property");
        Assert.Equal(ForbiddenMessage, error.GetString());
        Assert.Single(doc.RootElement.EnumerateObject());
    }

    /// <summary>
    /// Asserts the response is the 403 authorization answer (and NOT a 400 validation answer, NOT
    /// a login redirect and NOT a 500).
    /// </summary>
    private static async Task AssertForbiddenAsync(HttpResponseMessage response, string what)
    {
        Assert.True(
            response.StatusCode == HttpStatusCode.Forbidden,
            $"{what}: expected 403, got {(int)response.StatusCode} {response.StatusCode}. " +
            $"Body: {await response.Content.ReadAsStringAsync(Ct)}");
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Null(response.Headers.Location);
        await AssertForbiddenBodyAsync(response);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  1. ANONYMOUS — 401 with no redirect on every covered route
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// AN ANONYMOUS CALLER GETS 401 WITH NO REDIRECT on EVERY covered route — the release reads
    /// and mutations, the clarification reads and the answer, the whole backup surface, the whole
    /// configuration surface and the session list — and no <c>Location</c> header and no OAuth
    /// correlation cookie are ever produced.
    /// </summary>
    [Fact]
    public async Task AnonymousCaller_Gets401WithNoRedirect_OnEveryCoveredRoute()
    {
        await SeedAccountAsync(ViewerId, UserRole.Viewer);
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        await SeedAccountAsync(AdminId, UserRole.Admin);
        var releaseId = UniqueId("anon-rel");
        await SeedReleaseAsync(releaseId, "v1.0.0");

        using var client = NewClient();

        var requests = new (HttpMethod Method, string Url, Func<HttpContent>? Body)[]
        {
            // Releases.
            (HttpMethod.Get, $"/api/releases/{releaseId}", null),
            (HttpMethod.Get, $"/api/releases/{releaseId}/goals", null),
            (HttpMethod.Get, $"/api/releases/{releaseId}/validate", null),
            (HttpMethod.Post, "/api/releases/", () => Json(new { version = "v9.9.9" })),
            (HttpMethod.Patch, $"/api/releases/{releaseId}/status", () => Json(new { status = "Planning" })),
            (HttpMethod.Patch, $"/api/releases/{releaseId}/notes", () => Json(new { notes = "n" })),
            (HttpMethod.Patch, $"/api/releases/{releaseId}/tag", () => Json(new { tag = "v2" })),
            (HttpMethod.Patch, $"/api/releases/{releaseId}/repositories", () => Json(new { repositories = new[] { "r" } })),
            (HttpMethod.Delete, $"/api/releases/{releaseId}", null),
            // Clarifications.
            (HttpMethod.Get, "/api/clarifications/", null),
            (HttpMethod.Get, "/api/clarifications/pending", null),
            (HttpMethod.Get, "/api/clarifications/count", null),
            (HttpMethod.Post, "/api/clarifications/clar-1/answer", () => Json(new { answer = "a" })),
            // Backups.
            (HttpMethod.Get, "/api/backup/", null),
            (HttpMethod.Post, "/api/backup/", null),
            (HttpMethod.Get, "/api/backup/some.tar.gz", null),
            (HttpMethod.Post, "/api/backup/restore", () => Json(new { fileName = "some.tar.gz" })),
            // Configuration.
            (HttpMethod.Get, "/api/config/models", null),
            (HttpMethod.Get, "/api/config/models/discover", null),
            (HttpMethod.Get, "/api/config/repositories", null),
            (HttpMethod.Get, "/api/config/repositories/r/branches", null),
            (HttpMethod.Get, "/api/config/orchestrator", null),
            (HttpMethod.Get, "/api/config/workers", null),
            (HttpMethod.Get, "/api/config/composer", null),
            (HttpMethod.Patch, "/api/config/models", () => Json(new { orchestratorModel = "x" })),
            (HttpMethod.Post, "/api/config/available-models", () => Json(new { name = "m" })),
            (HttpMethod.Put, "/api/config/available-models/m", () => Json(new { name = "m" })),
            (HttpMethod.Delete, "/api/config/available-models/m", null),
            (HttpMethod.Post, "/api/config/sub-agent-models", () => Json(new { name = "m" })),
            (HttpMethod.Put, "/api/config/sub-agent-models/m", () => Json(new { name = "m" })),
            (HttpMethod.Delete, "/api/config/sub-agent-models/m", null),
            (HttpMethod.Post, "/api/config/repositories", () => Json(new { name = "r", url = "u", defaultBranch = "main" })),
            (HttpMethod.Put, "/api/config/repositories/r", () => Json(new { name = "r", url = "u", defaultBranch = "main" })),
            (HttpMethod.Delete, "/api/config/repositories/r", null),
            (HttpMethod.Patch, "/api/config/orchestrator", () => Json(new { maxIterations = 5 })),
            (HttpMethod.Patch, "/api/config/workers", () => Json(new Dictionary<string, int> { ["coder"] = 100 })),
            (HttpMethod.Patch, "/api/config/composer", () => Json(new { maxSteps = 75 })),
            // Sessions.
            (HttpMethod.Get, "/api/sessions", null),
        };

        foreach (var (method, url, body) in requests)
        {
            using var request = new HttpRequestMessage(method, url);
            if (body is not null)
                request.Content = body();

            var response = await client.SendAsync(request, Ct);

            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized,
                $"{method} {url} must answer 401 for an anonymous caller, got {(int)response.StatusCode}.");

            // NO redirect: no Location header and no OAuth handshake was started.
            Assert.Null(response.Headers.Location);
            Assert.False(
                response.Headers.TryGetValues("Set-Cookie", out var cookies)
                && cookies.Any(cookie => cookie.StartsWith(".AspNetCore.Correlation.", StringComparison.Ordinal)),
                $"{method} {url} must not start an OAuth handshake for a programmatic caller");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  2. The role × operation matrix — denied cells are 403, allowed cells SUCCEED
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE RELEASE MATRIX OVER HTTP: the two reads answer a VIEWER (200 with the real payload),
    /// every mutation is refused for a VIEWER and for a MEMBER (403 with the sanitized body), and
    /// an OPERATOR really performs every mutation (the VIABLE SUCCESS CONTROL — without it a
    /// route that refused everybody would satisfy the denial rows).
    /// </summary>
    [Fact]
    public async Task ReleaseMatrix_ViewerReadsSucceed_ViewerAndMemberMutationsAre403_OperatorMutationsSucceed()
    {
        await SeedAccountAsync(ViewerId, UserRole.Viewer);
        await SeedAccountAsync(MemberId, UserRole.Member);
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        var releaseId = UniqueId("rel");
        var emptyReleaseId = UniqueId("rel-empty");
        await SeedReleaseAsync(releaseId, "v1.0.0");
        await SeedGoalAsync(UniqueId("goal"), releaseId);
        // A SECOND, goal-free release: the delete contract refuses a release that still has
        // goals attached, so the delete success control needs a clean one.
        await SeedReleaseAsync(emptyReleaseId, "v0.9.0");

        using var client = NewClient();

        // ── SHARED READ: the Viewer really reads. ──
        using (var request = Authenticated(HttpMethod.Get, $"/api/releases/{releaseId}", ViewerId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var request = Authenticated(HttpMethod.Get, $"/api/releases/{releaseId}/goals", ViewerId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // ── EXECUTION OPERATOR: the Viewer and the Member are refused on EVERY mutation. ──
        foreach (var insufficient in new[] { ViewerId, MemberId })
        {
            var mutations = new (HttpMethod Method, string Url, Func<HttpContent>? Body, string What)[]
            {
                (HttpMethod.Post, "/api/releases/", () => Json(new { version = "v9.9.9" }), "create"),
                (HttpMethod.Patch, $"/api/releases/{releaseId}/status", () => Json(new { status = "Planning" }), "status"),
                (HttpMethod.Patch, $"/api/releases/{releaseId}/notes", () => Json(new { notes = "n" }), "notes"),
                (HttpMethod.Patch, $"/api/releases/{releaseId}/tag", () => Json(new { tag = "v2" }), "tag"),
                (HttpMethod.Patch, $"/api/releases/{releaseId}/repositories", () => Json(new { repositories = new[] { "r" } }), "repositories"),
                (HttpMethod.Delete, $"/api/releases/{releaseId}", null, "delete"),
                (HttpMethod.Get, $"/api/releases/{releaseId}/validate", null, "validate"),
            };

            foreach (var (method, url, body, what) in mutations)
            {
                using var request = body is null
                    ? Authenticated(method, url, insufficient)
                    : Authenticated(method, url, insufficient, body());
                var response = await client.SendAsync(request, Ct);
                await AssertForbiddenAsync(response, $"{insufficient} release {what}");
            }
        }

        // ── VIABLE SUCCESS CONTROL: the Operator really performs the mutations. ──
        using (var request = Authenticated(HttpMethod.Post, "/api/releases/", OperatorId,
                   Json(new { version = UniqueId("v") })))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        using (var request = Authenticated(HttpMethod.Patch, $"/api/releases/{releaseId}/notes", OperatorId,
                   Json(new { notes = "operator notes" })))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var request = Authenticated(HttpMethod.Patch, $"/api/releases/{releaseId}/tag", OperatorId,
                   Json(new { tag = "v1.1.0" })))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var request = Authenticated(HttpMethod.Patch, $"/api/releases/{releaseId}/repositories", OperatorId,
                   Json(new { repositories = new[] { "repo-a" } })))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var request = Authenticated(HttpMethod.Get, $"/api/releases/{releaseId}/validate", OperatorId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var request = Authenticated(HttpMethod.Delete, $"/api/releases/{emptyReleaseId}", OperatorId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
    }

    /// <summary>
    /// THE CONFIGURATION MATRIX OVER HTTP: EVERY projection and EVERY write is an administration
    /// surface, so a VIEWER, a MEMBER and an OPERATOR are all refused with 403 — and the ADMIN
    /// really reads the projection (the VIABLE SUCCESS CONTROL).
    /// </summary>
    [Fact]
    public async Task ConfigMatrix_EveryRoleBelowAdminIs403_AdminReadsAndWritesSucceed()
    {
        await SeedAccountAsync(ViewerId, UserRole.Viewer);
        await SeedAccountAsync(MemberId, UserRole.Member);
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        await SeedAccountAsync(AdminId, UserRole.Admin);

        using var client = NewClient();

        var reads = new[]
        {
            "/api/config/models",
            "/api/config/models/discover",
            "/api/config/repositories",
            "/api/config/repositories/r/branches",
            "/api/config/orchestrator",
            "/api/config/workers",
            "/api/config/composer",
        };

        foreach (var insufficient in new[] { ViewerId, MemberId, OperatorId })
        {
            foreach (var url in reads)
            {
                using var request = Authenticated(HttpMethod.Get, url, insufficient);
                var response = await client.SendAsync(request, Ct);
                await AssertForbiddenAsync(response, $"{insufficient} GET {url}");
            }
        }

        // ── VIABLE SUCCESS CONTROL: the Admin really reads the full projection. ──
        foreach (var url in reads)
        {
            // The discovery route has no discovery service in this host, so it answers with its own
            // service answer (500) rather than a refusal — still NOT a 403.
            using var request = Authenticated(HttpMethod.Get, url, AdminId);
            var response = await client.SendAsync(request, Ct);
            Assert.NotEqual(
                HttpStatusCode.Forbidden,
                response.StatusCode);
            Assert.NotEqual(
                HttpStatusCode.Unauthorized,
                response.StatusCode);
        }

        // The projections that need no external service answer 200 with the real payload.
        foreach (var url in new[]
        {
            "/api/config/models",
            "/api/config/repositories",
            "/api/config/orchestrator",
            "/api/config/workers",
            "/api/config/composer",
        })
        {
            using var request = Authenticated(HttpMethod.Get, url, AdminId);
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    /// <summary>
    /// THE BACKUP MATRIX OVER HTTP: EVERY backup route — list, create, download and restore — is
    /// an administration surface, so a VIEWER, a MEMBER and an OPERATOR are all refused with 403 —
    /// and the ADMIN really lists and creates (the VIABLE SUCCESS CONTROL).
    /// </summary>
    [Fact]
    public async Task BackupMatrix_EveryRoleBelowAdminIs403_AdminListsAndCreates()
    {
        await SeedAccountAsync(ViewerId, UserRole.Viewer);
        await SeedAccountAsync(MemberId, UserRole.Member);
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        await SeedAccountAsync(AdminId, UserRole.Admin);

        using var client = NewClient();

        var routes = new (HttpMethod Method, string Url, Func<HttpContent>? Body)[]
        {
            (HttpMethod.Get, "/api/backup/", null),
            (HttpMethod.Post, "/api/backup/", null),
            (HttpMethod.Get, "/api/backup/some.tar.gz", null),
            (HttpMethod.Post, "/api/backup/restore", () => Json(new { fileName = "some.tar.gz" })),
        };

        foreach (var insufficient in new[] { ViewerId, MemberId, OperatorId })
        {
            foreach (var (method, url, body) in routes)
            {
                using var request = body is null
                    ? Authenticated(method, url, insufficient)
                    : Authenticated(method, url, insufficient, body());
                var response = await client.SendAsync(request, Ct);
                await AssertForbiddenAsync(response, $"{insufficient} {method} {url}");
            }
        }

        // ── VIABLE SUCCESS CONTROL: the Admin really lists and creates. ──
        using (var request = Authenticated(HttpMethod.Get, "/api/backup/", AdminId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var request = Authenticated(HttpMethod.Post, "/api/backup/", AdminId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    /// <summary>
    /// THE CLARIFICATION MATRIX OVER HTTP: the three reads answer a VIEWER (200 with the real
    /// payload), answering is refused for a VIEWER and a MEMBER (403), and an OPERATOR really
    /// answers (the VIABLE SUCCESS CONTROL).
    /// </summary>
    [Fact]
    public async Task ClarificationMatrix_ViewerReadsSucceed_ViewerAndMemberAnswersAre403_OperatorAnswerSucceeds()
    {
        await SeedAccountAsync(ViewerId, UserRole.Viewer);
        await SeedAccountAsync(MemberId, UserRole.Member);
        await SeedAccountAsync(OperatorId, UserRole.Operator);

        var clarificationId = UniqueId("clar");
        SeedClarification(clarificationId);

        using var client = NewClient();

        // ── SHARED READ: the Viewer really reads. ──
        foreach (var url in new[] { "/api/clarifications/", "/api/clarifications/pending", "/api/clarifications/count" })
        {
            using var request = Authenticated(HttpMethod.Get, url, ViewerId);
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // ── EXECUTION OPERATOR: the Viewer and the Member are refused on the answer. ──
        foreach (var insufficient in new[] { ViewerId, MemberId })
        {
            using var request = Authenticated(HttpMethod.Post, $"/api/clarifications/{clarificationId}/answer",
                insufficient, Json(new { answer = "main" }));
            var response = await client.SendAsync(request, Ct);
            await AssertForbiddenAsync(response, $"{insufficient} clarification answer");
        }

        // ── VIABLE SUCCESS CONTROL: the Operator really answers. ──
        using (var request = Authenticated(HttpMethod.Post, $"/api/clarifications/{clarificationId}/answer",
                   OperatorId, Json(new { answer = "main" })))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  3. The demoted / disabled / unknown caller
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A DEMOTED CALLER GETS 403 ON THE VERY NEXT REQUEST: an Operator demoted to Viewer loses
    /// every release mutation immediately, and the answer is the authorization answer (403), never
    /// the validation answer (400).
    /// </summary>
    [Fact]
    public async Task DemotedCaller_Gets403_OnTheVeryNextRequest()
    {
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        var releaseId = UniqueId("demoted-rel");
        await SeedReleaseAsync(releaseId, "v1.0.0");

        using var client = NewClient();

        // BEFORE the demotion the Operator is authorized.
        using (var request = Authenticated(HttpMethod.Patch, $"/api/releases/{releaseId}/notes", OperatorId,
                   Json(new { notes = "before" })))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await SetRoleAsync(OperatorId, UserRole.Viewer);

        using (var request = Authenticated(HttpMethod.Patch, $"/api/releases/{releaseId}/notes", OperatorId,
                   Json(new { notes = "after" })))
        {
            var response = await client.SendAsync(request, Ct);
            await AssertForbiddenAsync(response, "demoted operator release notes");
        }

        using (var request = Authenticated(HttpMethod.Delete, $"/api/releases/{releaseId}", OperatorId))
        {
            var response = await client.SendAsync(request, Ct);
            await AssertForbiddenAsync(response, "demoted operator release delete");
        }
    }

    /// <summary>
    /// A DISABLED CALLER GETS 403 ON THE VERY NEXT REQUEST — including on the READS a disabled
    /// account used to hold, because a disabled account holds no capability at all.
    /// </summary>
    [Fact]
    public async Task DisabledCaller_Gets403_OnTheVeryNextRequest()
    {
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        await SeedAccountAsync(AdminId, UserRole.Admin);
        var releaseId = UniqueId("disabled-rel");
        await SeedReleaseAsync(releaseId, "v1.0.0");

        using var client = NewClient();

        // BEFORE the disablement the Operator may read and mutate.
        using (var request = Authenticated(HttpMethod.Get, $"/api/releases/{releaseId}", OperatorId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await SetEnabledAsync(OperatorId, enabled: false);

        using (var request = Authenticated(HttpMethod.Get, $"/api/releases/{releaseId}", OperatorId))
        {
            var response = await client.SendAsync(request, Ct);
            await AssertForbiddenAsync(response, "disabled operator release read");
        }

        using (var request = Authenticated(HttpMethod.Delete, $"/api/releases/{releaseId}", OperatorId))
        {
            var response = await client.SendAsync(request, Ct);
            await AssertForbiddenAsync(response, "disabled operator release delete");
        }

        // The DISABLED ADMIN loses the administration surface too.
        await SetEnabledAsync(AdminId, enabled: false);
        using (var request = Authenticated(HttpMethod.Get, "/api/config/models", AdminId))
        {
            var response = await client.SendAsync(request, Ct);
            await AssertForbiddenAsync(response, "disabled admin config read");
        }
    }

    /// <summary>
    /// AN UNKNOWN ACCOUNT (an authenticated identity with NO persisted row) IS REFUSED with 403 on
    /// every covered surface — it is not silently treated as an administrator.
    /// </summary>
    [Fact]
    public async Task UnknownAccount_Gets403_OnEveryCoveredSurface()
    {
        await SeedAccountAsync(ViewerId, UserRole.Viewer);

        using var client = NewClient();

        var requests = new (HttpMethod Method, string Url, Func<HttpContent>? Body)[]
        {
            (HttpMethod.Get, "/api/releases/rel-1", null),
            (HttpMethod.Patch, "/api/releases/rel-1/notes", () => Json(new { notes = "n" })),
            (HttpMethod.Get, "/api/clarifications/", null),
            (HttpMethod.Post, "/api/clarifications/clar-1/answer", () => Json(new { answer = "a" })),
            (HttpMethod.Get, "/api/backup/", null),
            (HttpMethod.Get, "/api/config/models", null),
        };

        foreach (var (method, url, body) in requests)
        {
            using var request = body is null
                ? Authenticated(method, url, UnknownId)
                : Authenticated(method, url, UnknownId, body());
            var response = await client.SendAsync(request, Ct);
            await AssertForbiddenAsync(response, $"unknown account {method} {url}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  4. The pre-binding contract — malformed body, unauthorized caller
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A MALFORMED BODY FROM AN UNAUTHORIZED CALLER GETS 403 (never 400) on EVERY covered
    /// MUTATING route that takes a typed body — the body never reaches the binder — while an
    /// AUTHORIZED caller keeps the existing 400 binding answer.
    /// <para>
    /// The unauthorized caller here is a VIEWER, who is insufficient on every one of these routes
    /// (the configuration and backup writes need an ADMIN; the release mutations and the
    /// clarification answer need an OPERATOR). The authorized caller is therefore the ADMIN, who
    /// holds every one of these capabilities.
    /// </para>
    /// </summary>
    [Fact]
    public async Task MalformedBody_FromAnUnauthorizedCaller_Gets403BeforeBinding_OnEveryMutatingRoute()
    {
        await SeedAccountAsync(ViewerId, UserRole.Viewer);
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        await SeedAccountAsync(AdminId, UserRole.Admin);
        var releaseId = UniqueId("rel");
        await SeedReleaseAsync(releaseId, "v1.0.0");

        using var client = NewClient();

        const string malformed = "{ this is not json";

        // Every covered MUTATING route that binds a TYPED BODY.
        var mutatingRoutes = new (HttpMethod Method, string Url)[]
        {
            (HttpMethod.Post, "/api/releases/"),
            (HttpMethod.Patch, $"/api/releases/{releaseId}/status"),
            (HttpMethod.Patch, $"/api/releases/{releaseId}/notes"),
            (HttpMethod.Patch, $"/api/releases/{releaseId}/tag"),
            (HttpMethod.Patch, $"/api/releases/{releaseId}/repositories"),
            (HttpMethod.Post, "/api/clarifications/clar-1/answer"),
            (HttpMethod.Post, "/api/backup/restore"),
            (HttpMethod.Patch, "/api/config/models"),
            (HttpMethod.Post, "/api/config/available-models"),
            (HttpMethod.Put, "/api/config/available-models/m"),
            (HttpMethod.Post, "/api/config/sub-agent-models"),
            (HttpMethod.Put, "/api/config/sub-agent-models/m"),
            (HttpMethod.Post, "/api/config/repositories"),
            (HttpMethod.Put, "/api/config/repositories/r"),
            (HttpMethod.Patch, "/api/config/orchestrator"),
            (HttpMethod.Patch, "/api/config/workers"),
            (HttpMethod.Patch, "/api/config/composer"),
        };

        foreach (var (method, url) in mutatingRoutes)
        {
            // The VIEWER is insufficient on every one of these routes.
            using (var request = Authenticated(method, url, ViewerId, Raw(malformed)))
            {
                var response = await client.SendAsync(request, Ct);
                await AssertForbiddenAsync(response, $"malformed body, viewer, {method} {url}");
            }

            // The AUTHORIZED caller keeps the pre-existing 400 binding answer (a malformed body
            // reaches the binder, which rejects it).
            using (var request = Authenticated(method, url, AdminId, Raw(malformed)))
            {
                var response = await client.SendAsync(request, Ct);
                Assert.True(
                    response.StatusCode == HttpStatusCode.BadRequest,
                    $"malformed body, admin, {method} {url}: expected 400, got {(int)response.StatusCode}.");
            }
        }
    }

    /// <summary>
    /// THE PRE-BINDING GATE IS THE ONE THAT REFUSES: an unauthorized caller with a malformed body
    /// gets 403 while an authorized caller with the SAME malformed body gets 400 — so the 403 is
    /// produced by the authorization gate and not by a body-sniffing heuristic.
    /// </summary>
    [Fact]
    public async Task MalformedBody_UnauthorizedIs403_AuthorizedIs400_OnTheBackupRestoreRoute()
    {
        await SeedAccountAsync(ViewerId, UserRole.Viewer);
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        await SeedAccountAsync(AdminId, UserRole.Admin);

        using var client = NewClient();

        const string malformed = "{ this is not json";

        foreach (var insufficient in new[] { ViewerId, OperatorId })
        {
            using var request = Authenticated(HttpMethod.Post, "/api/backup/restore",
                insufficient, Raw(malformed));
            var response = await client.SendAsync(request, Ct);

            Assert.True(
                response.StatusCode == HttpStatusCode.Forbidden,
                $"POST /api/backup/restore with a malformed body from {insufficient} must answer 403 " +
                $"(the pre-binding gate), but answered {(int)response.StatusCode}.");
            await AssertForbiddenBodyAsync(response);
        }

        // The ADMIN keeps the existing 400 binding answer.
        using (var request = Authenticated(HttpMethod.Post, "/api/backup/restore", AdminId, Raw(malformed)))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  5. The existing 409 answers are preserved
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE EXISTING 409 ANSWER IS PRESERVED FOR AN AUTHORIZED CALLER: a duplicate release
    /// creation still answers 409 with the store's message — the authorization gate did not
    /// swallow or reshape it.
    /// </summary>
    [Fact]
    public async Task AuthorizedCaller_Existing409AnswerIsPreserved()
    {
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        var duplicateId = UniqueId("dup-rel");
        await SeedReleaseAsync(duplicateId, "v1.0.0");

        using var client = NewClient();

        using (var request = Authenticated(HttpMethod.Post, "/api/releases/", OperatorId,
                   Json(new { version = duplicateId })))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync(Ct);
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.TryGetProperty("error", out var error));
            Assert.Contains(duplicateId, error.GetString(), StringComparison.Ordinal);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  6. The response shapes are preserved
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE SUCCESSFUL RESPONSE SHAPES ARE PRESERVED EXACTLY: the release read carries the release
    /// DTO's property set, the release goals read carries the DOMAIN goal list, the release status
    /// no-op carries the bare release JSON, the clarification reads carry their own bodies, the
    /// backup list carries the <c>BackupInfo</c> shape and the session list carries the shared
    /// execution sessions only.
    /// </summary>
    [Fact]
    public async Task AuthorizedSuccessPaths_KeepTheirResponseShapes()
    {
        await SeedAccountAsync(ViewerId, UserRole.Viewer);
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        await SeedAccountAsync(AdminId, UserRole.Admin);
        var releaseId = UniqueId("shape-rel");
        var goalId = UniqueId("shape-goal");
        var clarificationId = UniqueId("shape-clar");
        await SeedReleaseAsync(releaseId, "v1.0.0");
        await SeedGoalAsync(goalId, releaseId);
        SeedClarification(clarificationId);

        using var client = NewClient();

        // ── The release read: the release DTO's property set, unchanged. ──
        using (var request = Authenticated(HttpMethod.Get, $"/api/releases/{releaseId}", ViewerId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal(
                new[] { "id", "tag", "status", "notes", "createdAt", "releasedAt", "repositoryNames", "executionState" },
                doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal(releaseId, doc.RootElement.GetProperty("id").GetString());
            Assert.Equal("v1.0.0", doc.RootElement.GetProperty("tag").GetString());
            Assert.Equal("planning", doc.RootElement.GetProperty("status").GetString());
        }

        // ── The release goals read: the DOMAIN goal list (never a DTO). ──
        using (var request = Authenticated(HttpMethod.Get, $"/api/releases/{releaseId}/goals", ViewerId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            var goal = Assert.Single(doc.RootElement.EnumerateArray().ToList());
            Assert.Equal(goalId, goal.GetProperty("id").GetString());
            Assert.True(goal.TryGetProperty("reviewStatus", out _), "the domain goal shape is preserved");
        }

        // ── The release status no-op: the bare release JSON (not a {release,result} envelope). ──
        using (var request = Authenticated(HttpMethod.Patch, $"/api/releases/{releaseId}/status", OperatorId,
                   Json(new { status = "Planning" })))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal(
                new[] { "id", "tag", "status", "notes", "createdAt", "releasedAt", "repositoryNames", "executionState" },
                doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        }

        // ── The clarification reads: the queue's own request list, and the {count} body. ──
        using (var request = Authenticated(HttpMethod.Get, "/api/clarifications/", ViewerId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            var clar = Assert.Single(doc.RootElement.EnumerateArray().ToList());
            Assert.Equal(clarificationId, clar.GetProperty("id").GetString());
            Assert.True(clar.TryGetProperty("question", out _));
            Assert.True(clar.TryGetProperty("status", out _));
        }

        using (var request = Authenticated(HttpMethod.Get, "/api/clarifications/count", ViewerId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            Assert.True(doc.RootElement.TryGetProperty("count", out var count));
            Assert.Equal(JsonValueKind.Number, count.ValueKind);
        }

        // ── The backup list: the BackupInfo shape. ──
        using (var request = Authenticated(HttpMethod.Get, "/api/backup/", AdminId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        }

        // ── The configuration projections: their DTO property sets, unchanged. ──
        using (var request = Authenticated(HttpMethod.Get, "/api/config/models", AdminId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal(
                new[]
                {
                    "orchestrator", "composer", "compaction", "workers",
                    "orchestratorReasoningEffort", "composerReasoningEffort",
                    "workerReasoningEffort", "workerPremiumReasoningEffort",
                    "subAgentModelReasoning", "availableModels", "subAgentModels",
                },
                doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        }

        using (var request = Authenticated(HttpMethod.Get, "/api/config/orchestrator", AdminId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.True(doc.RootElement.TryGetProperty("model", out _));
            Assert.True(doc.RootElement.TryGetProperty("maxIterations", out _));
            Assert.True(doc.RootElement.TryGetProperty("brainMaxSteps", out _));
        }

        using (var request = Authenticated(HttpMethod.Get, "/api/config/workers", AdminId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            // The TOP-LEVEL role-keyed dictionary (never a wrapper object).
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        }

        using (var request = Authenticated(HttpMethod.Get, "/api/config/composer", AdminId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.True(doc.RootElement.TryGetProperty("model", out _));
            Assert.True(doc.RootElement.TryGetProperty("maxSteps", out _));
            Assert.True(doc.RootElement.TryGetProperty("eventNotifications", out _));
        }

        // ── The session list: the shared execution sessions only (the Composer entry is absent). ──
        var (executionId, composerId) = SeedSessions();
        using (var request = Authenticated(HttpMethod.Get, "/api/sessions", ViewerId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            var ids = doc.RootElement.EnumerateArray()
                .Select(e => e.GetProperty("sessionId").GetString())
                .ToList();
            Assert.Contains(executionId, ids);
            Assert.DoesNotContain(composerId, ids);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  7. The session list is capability-gated (SharedRead)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>GET /api/sessions</c> IS A SHARED-READ SURFACE: it resolves the caller FRESH through
    /// the same authorization seam as every other covered route, so a DISABLED account and an
    /// UNKNOWN account are refused with 403 — while an eligible VIEWER still receives the
    /// Composer-filtered list.
    /// <para>
    /// The gate must be a FRESH resolution, not a one-shot snapshot: the demotion-to-Viewer row
    /// below proves the read still works for an account whose role CHANGED between two calls.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SessionsRoute_RefusesDisabledAndUnknownAccounts_AndServesEligibleCallers()
    {
        await SeedAccountAsync(ViewerId, UserRole.Viewer);
        await SeedAccountAsync(AdminId, UserRole.Admin);

        using var client = NewClient();

        // ── The eligible VIEWER is served. ──
        using (var request = Authenticated(HttpMethod.Get, "/api/sessions", ViewerId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // ── A DISABLED account holds no capability at all. ──
        await SetEnabledAsync(AdminId, enabled: false);
        using (var request = Authenticated(HttpMethod.Get, "/api/sessions", AdminId))
        {
            var response = await client.SendAsync(request, Ct);
            await AssertForbiddenAsync(response, "disabled account GET /api/sessions");
        }

        // ── An UNKNOWN account has no persisted row at all. ──
        using (var request = Authenticated(HttpMethod.Get, "/api/sessions", UnknownId))
        {
            var response = await client.SendAsync(request, Ct);
            await AssertForbiddenAsync(response, "unknown account GET /api/sessions");
        }
    }

    /// <summary>
    /// THE SESSION GATE RE-RESOLVES FRESHLY: an account that is DEMOTED between two calls keeps
    /// its shared-read access (a Viewer may read) and is therefore NOT refused — the point being
    /// that the gate asks the directory on every call rather than caching a snapshot taken at the
    /// first call. A caller demoted BELOW shared read (there is none — Viewer is the floor) is
    /// covered by the disablement row.
    /// </summary>
    [Fact]
    public async Task SessionsRoute_ReResolvesFreshly_SoADemotedViewerStillReads()
    {
        await SeedAccountAsync(AdminId, UserRole.Admin);

        using var client = NewClient();

        // BEFORE the demotion the Admin is served.
        using (var request = Authenticated(HttpMethod.Get, "/api/sessions", AdminId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // DEMOTED to Viewer: a Viewer still holds shared read, so the read still works — which
        // proves the gate consulted the CURRENT role rather than a cached one (a cached
        // "allowed" snapshot would look identical, so the DISABLEMENT row above is the load-
        // bearing half of this pair).
        await SetRoleAsync(AdminId, UserRole.Viewer);
        using (var request = Authenticated(HttpMethod.Get, "/api/sessions", AdminId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // DISABLED: the very next call is refused.
        await SetEnabledAsync(AdminId, enabled: false);
        using (var request = Authenticated(HttpMethod.Get, "/api/sessions", AdminId))
        {
            var response = await client.SendAsync(request, Ct);
            await AssertForbiddenAsync(response, "disabled account GET /api/sessions");
        }
    }

    /// <summary>
    /// <c>GET /api/sessions</c> REPORTS SHARED EXECUTION SESSIONS ONLY: with a registry holding
    /// the THREE execution session types (Brain, BrainGoal and GoalReview) AND a Composer entry,
    /// the endpoint omits exactly the Composer one — while
    /// <see cref="CopilotHive.Dashboard.LlmSessionRegistry.GetAll"/> STILL returns all four, so
    /// the registry's semantics are provably unchanged and the filter is endpoint-local.
    /// </summary>
    [Fact]
    public async Task SessionsRoute_ExcludesComposerEntries_WhileTheRegistryKeepsThem()
    {
        await SeedAccountAsync(ViewerId, UserRole.Viewer);

        var registry = _factory.Services.GetRequiredService<CopilotHive.Dashboard.LlmSessionRegistry>();

        var ids = new List<string>();
        foreach (var type in new[]
        {
            CopilotHive.Dashboard.LlmSessionType.Brain,
            CopilotHive.Dashboard.LlmSessionType.BrainGoal,
            CopilotHive.Dashboard.LlmSessionType.GoalReview,
            CopilotHive.Dashboard.LlmSessionType.Composer,
        })
        {
            var id = $"matrix-session-{type}-{Guid.NewGuid():N}";
            registry.RegisterOrUpdate(new CopilotHive.Dashboard.LlmSessionInfo
            {
                SessionId = id,
                SessionType = type,
                Model = "m",
                Status = "active",
                MaxTokens = 1,
            });
            ids.Add(id);
        }

        try
        {
            // The REGISTRY keeps all four — including the Composer entry.
            var all = registry.GetAll();
            foreach (var id in ids)
                Assert.Contains(all, s => s.SessionId == id);
            Assert.Contains(all, s => s.SessionType == CopilotHive.Dashboard.LlmSessionType.Composer);

            using var client = NewClient();
            using var request = Authenticated(HttpMethod.Get, "/api/sessions", ViewerId);
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            var reported = doc.RootElement.EnumerateArray()
                .Select(e => e.GetProperty("sessionId").GetString())
                .ToList();

            // The three EXECUTION sessions are reported.
            Assert.Contains(ids[0], reported);
            Assert.Contains(ids[1], reported);
            Assert.Contains(ids[2], reported);

            // The COMPOSER session is NOT.
            Assert.DoesNotContain(ids[3], reported);
            Assert.DoesNotContain(
                doc.RootElement.EnumerateArray(),
                e => e.GetProperty("sessionType").GetString() == nameof(CopilotHive.Dashboard.LlmSessionType.Composer));
        }
        finally
        {
            foreach (var id in ids)
                registry.Unregister(id);
        }
    }

    public void Dispose() => _factory.Dispose();
}
