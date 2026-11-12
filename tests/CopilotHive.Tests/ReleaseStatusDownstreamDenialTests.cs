using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

using CopilotHive.Goals;
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
/// THE RELEASE-STATUS DOWNSTREAM-DENIAL PATH — the second of the two INDEPENDENT gates on
/// <c>PATCH /api/releases/{id}/status</c>.
/// <para>
/// The route carries <c>PreBindingAuthorization(ExecutionOperator)</c>, so an insufficient caller is
/// refused BEFORE the body binds. That gate, however, CANNOT refuse a caller whose role changed
/// AFTER it ran: the facade's own per-call resolution is the second, independent gate. When the
/// facade refuses, the outcome is a <see cref="StatusFailureOutcome"/> with
/// <see cref="FacadeErrorKind.Forbidden"/> — and the route's status mapper must turn it into the
/// 403 authorization answer, not the unexpected-kind 500 it produced before the fix.
/// </para>
/// <para>
/// <b>HOW THESE ROWS REACH THE DOWNSTREAM PATH.</b> A row that merely used an insufficient caller
/// would be intercepted by the pre-binding middleware and would never reach the mapper at all — it
/// would prove nothing about the fix. These rows instead perform the directory state change INSIDE
/// the handler, immediately before the real facade's own resolution runs: the pre-binding gate has
/// already answered "allowed" (the account was still an Operator when it did), and the facade then
/// re-resolves and refuses. The state change is a REAL directory write through the production EF
/// model; only its timing is deterministic.
/// </para>
/// </summary>
[Collection("HiveIntegration")]
public sealed class ReleaseStatusDownstreamDenialTests : IDisposable
{
    private const string IdentityHeader = "X-Test-GitHub-Id";

    private const string OperatorId = "downstream-operator";
    private const string AdminId = "downstream-admin";

    private const string ForbiddenMessage = "The acting account is not authorized to perform this operation.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The REAL host, booted with OAuth CONFIGURED so the production authentication/authorization
    /// pipeline and the path-aware challenge handler are wired exactly as in production, with the
    /// default AUTHENTICATION SCHEME replaced by a test scheme that reads the identity from a
    /// request header.
    /// </summary>
    private sealed class AuthApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _stateDir =
            Path.Combine(Path.GetTempPath(), $"copilothive-downstream-{Guid.NewGuid():N}");
        private readonly string? _previousStateDir;
        private readonly string? _previousClientId;
        private readonly string? _previousClientSecret;
        private readonly string? _previousAllowInsecure;

        /// <summary>
        /// The mutation the facade decorator performs INSIDE the handler, right before the real
        /// facade's fresh resolution — i.e. exactly between the two gates. It is a REAL directory
        /// write through the production EF model.
        /// </summary>
        internal Action? BeforeFacadeResolution { get; set; }

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

                // The facade decorator: it performs the directory state change INSIDE the handler
                // and then forwards to the REAL production facade, so the outcome the route maps is
                // the real facade's real fresh refusal — not a hand-built stub.
                services.AddSingleton<IReleaseFacade>(sp =>
                    new MutatingReleaseFacade(
                        sp.GetRequiredService<RealReleaseFacadeMarker>().Inner,
                        () => BeforeFacadeResolution?.Invoke()));

                services.AddSingleton<RealReleaseFacadeMarker>(sp => new RealReleaseFacadeMarker(
                    new ReleaseFacade(
                        sp.GetRequiredService<IGoalStore>(),
                        sp.GetRequiredService<CopilotHive.Dashboard.DashboardNotifier>(),
                        sp.GetRequiredService<ILogger<ReleaseFacade>>(),
                        sp.GetRequiredService<HivePrincipalResolver>(),
                        sp.GetRequiredService<HivePolicy>(),
                        sp.GetService<ReleaseExecutionService>(),
                        sp.GetService<IEventBus>(),
                        sp.GetService<CopilotHive.Services.NuGetPublishMonitorService>(),
                        sp.GetService<CopilotHive.Configuration.HiveConfigFile>(),
                        appLifetime: null,
                        sp.GetService<CopilotHive.Services.KnowledgeDocumentCleanupService>())));

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
    /// A marker holding the REAL production <see cref="ReleaseFacade"/> the decorator forwards to.
    /// </summary>
    private sealed class RealReleaseFacadeMarker(ReleaseFacade inner)
    {
        internal ReleaseFacade Inner { get; } = inner;
    }

    /// <summary>
    /// An <see cref="IReleaseFacade"/> decorator that performs the directory state change inside the
    /// handler and then forwards to the REAL facade. Every other operation is forwarded untouched.
    /// </summary>
    private sealed class MutatingReleaseFacade(ReleaseFacade inner, Action mutate) : IReleaseFacade
    {
        public Task<ReleaseStatusOutcome> UpdateReleaseStatusAsync(
            string id, UpdateReleaseStatusRequest request, ClaimsPrincipal? actingUser, CancellationToken ct)
        {
            // The state change lands HERE: after the pre-binding gate answered "allowed" and before
            // the real facade's fresh resolution runs.
            mutate();
            return inner.UpdateReleaseStatusAsync(id, request, actingUser, ct);
        }

        public Task<FacadeResult<ReleaseDto>> GetReleaseAsync(
            string id, ClaimsPrincipal? actingUser, CancellationToken ct) =>
            inner.GetReleaseAsync(id, actingUser, ct);

        public Task<FacadeResult<IReadOnlyList<Goal>>> GetGoalsByReleaseAsync(
            string releaseId, ClaimsPrincipal? actingUser, CancellationToken ct) =>
            inner.GetGoalsByReleaseAsync(releaseId, actingUser, ct);

        public Task<FacadeResult<ReleaseDto>> CreateReleaseAsync(
            CreateReleaseRequest request, ClaimsPrincipal? actingUser) =>
            inner.CreateReleaseAsync(request, actingUser);

        public Task<FacadeResult<ReleaseDto>> UpdateReleaseNotesAsync(
            string id, UpdateReleaseNotesRequest request, ClaimsPrincipal? actingUser) =>
            inner.UpdateReleaseNotesAsync(id, request, actingUser);

        public Task<FacadeResult<ReleaseDto>> UpdateReleaseTagAsync(
            string id, UpdateReleaseTagRequest request, ClaimsPrincipal? actingUser) =>
            inner.UpdateReleaseTagAsync(id, request, actingUser);

        public Task<FacadeResult<ReleaseDto>> UpdateReleaseRepositoriesAsync(
            string id, UpdateReleaseRepositoriesRequest request, ClaimsPrincipal? actingUser) =>
            inner.UpdateReleaseRepositoriesAsync(id, request, actingUser);

        public Task<FacadeResult<RemovedResult>> DeleteReleaseAsync(
            string id, ClaimsPrincipal? actingUser, CancellationToken ct) =>
            inner.DeleteReleaseAsync(id, actingUser, ct);

        public Task<FacadeResult<ValidationDto>> ValidateReleaseAsync(
            string id, ClaimsPrincipal? actingUser, CancellationToken ct) =>
            inner.ValidateReleaseAsync(id, actingUser, ct);
    }

    /// <summary>
    /// The test authentication scheme: it authenticates a request that carries the identity header
    /// and refuses every other request.
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
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }

    private readonly AuthApiFactory _factory = new();

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
    });

    // ── directory helpers ───────────────────────────────────────────────────

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

    /// <summary>A REAL directory write demoting the account — the write an administrator makes.</summary>
    private void Demote(string gitHubId)
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CopilotHiveDbContext>>();
        using var db = factory.CreateDbContext();
        var entity = db.Users.Single(u => u.GitHubId == gitHubId);
        entity.Role = UserRole.Viewer;
        db.SaveChanges();
    }

    /// <summary>A REAL directory write disabling the account.</summary>
    private void Disable(string gitHubId)
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CopilotHiveDbContext>>();
        using var db = factory.CreateDbContext();
        var entity = db.Users.Single(u => u.GitHubId == gitHubId);
        entity.Enabled = false;
        db.SaveChanges();
    }

    private async Task<string> SeedReleaseAsync(string tag)
    {
        var id = $"rel-{Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
        await store.CreateReleaseAsync(new Release { Id = id, Tag = tag }, Ct);
        return id;
    }

    /// <summary>A plain status request (no directory mutation).</summary>
    private static HttpRequestMessage StatusRequest(string releaseId, string gitHubId, string status)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/releases/{releaseId}/status")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { status }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(IdentityHeader, gitHubId);
        return request;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  1. The demotion between the two gates
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A CALLER DEMOTED BETWEEN THE TWO GATES GETS THE 403 AUTHORIZATION ANSWER — never a 500 and
    /// never a 400.
    /// <para>
    /// THE ROW REALLY GETS PAST PRE-BINDING: the account is a genuine Operator when the pre-binding
    /// middleware evaluates it (the demotion happens later, inside the handler), so the pre-binding
    /// gate answers "allowed". The refusal therefore comes from the REAL facade's fresh resolution,
    /// and it must travel through the status-failure mapper — the exact branch that threw before the
    /// fix.
    /// </para>
    /// </summary>
    [Fact]
    public async Task StatusPatch_CallerDemotedBetweenTheTwoGates_Gets403_NotA500()
    {
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        var releaseId = await SeedReleaseAsync("v1.0.0");

        using var client = NewClient();

        // The POSITIVE CONTROL through the SAME handler: with the account's state UNCHANGED the
        // Operator is authorized and the request is answered on the merits (Planning -> Planning is
        // the no-op branch, so the response is the bare release JSON). Without this control a row
        // that merely observed "not a 500" could be satisfied by a route that refuses everybody.
        _factory.BeforeFacadeResolution = null;
        using (var request = StatusRequest(releaseId, OperatorId, "Planning"))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal(releaseId, doc.RootElement.GetProperty("id").GetString());
        }

        // THE STATE CHANGE, landing between the pre-binding gate and the facade's resolution.
        _factory.BeforeFacadeResolution = () => Demote(OperatorId);
        using (var request = StatusRequest(releaseId, OperatorId, "Planning"))
        {
            var response = await client.SendAsync(request, Ct);

            Assert.True(
                response.StatusCode == HttpStatusCode.Forbidden,
                "a caller demoted between the pre-binding gate and the facade's fresh resolution must " +
                $"get 403, got {(int)response.StatusCode} {response.StatusCode}. " +
                $"Body: {await response.Content.ReadAsStringAsync(Ct)}");

            // The EXACT sanitized denial text — no detail, no results, no errors.
            var body = await response.Content.ReadAsStringAsync(Ct);
            using var doc = JsonDocument.Parse(body);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            Assert.True(doc.RootElement.TryGetProperty("error", out var error),
                "the 403 body must carry an 'error' property");
            Assert.Equal(ForbiddenMessage, error.GetString());
            Assert.Single(doc.RootElement.EnumerateObject());
        }

        // The release was NOT touched by the refused request.
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            var stored = await store.GetReleaseAsync(releaseId, Ct);
            Assert.NotNull(stored);
            Assert.Equal(ReleaseStatus.Planning, stored!.Status);
            Assert.Null(stored.ReleasedAt);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  2. The disablement between the two gates
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A CALLER DISABLED BETWEEN THE TWO GATES GETS THE 403 AUTHORIZATION ANSWER — the disablement
    /// needs no role change, and the answer is the same sanitized body.
    /// </summary>
    [Fact]
    public async Task StatusPatch_CallerDisabledBetweenTheTwoGates_Gets403_NotA500()
    {
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        var releaseId = await SeedReleaseAsync("v1.0.0");

        using var client = NewClient();

        // The POSITIVE CONTROL through the same handler.
        _factory.BeforeFacadeResolution = null;
        using (var request = StatusRequest(releaseId, OperatorId, "Planning"))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // THE STATE CHANGE, landing between the two gates.
        _factory.BeforeFacadeResolution = () => Disable(OperatorId);
        using (var request = StatusRequest(releaseId, OperatorId, "Planning"))
        {
            var response = await client.SendAsync(request, Ct);

            Assert.True(
                response.StatusCode == HttpStatusCode.Forbidden,
                "a caller disabled between the pre-binding gate and the facade's fresh resolution " +
                $"must get 403, got {(int)response.StatusCode} {response.StatusCode}. " +
                $"Body: {await response.Content.ReadAsStringAsync(Ct)}");

            var body = await response.Content.ReadAsStringAsync(Ct);
            using var doc = JsonDocument.Parse(body);
            Assert.Equal(ForbiddenMessage, doc.RootElement.GetProperty("error").GetString());
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  3. The refusal is caused by the CALLER's state, not by the route
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE REFUSAL IS CAUSED BY THE CALLER'S STATE — not by the route refusing everybody: an ADMIN
    /// performing the very same operation, through the very same handler, in the very same request
    /// shape, is still served. Without this control the rows above could be satisfied by a status
    /// route that refused all callers.
    /// </summary>
    [Fact]
    public async Task StatusPatch_TheDownstreamRefusalIsCausedByTheCallersState_NotByTheRoute()
    {
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        await SeedAccountAsync(AdminId, UserRole.Admin);
        var releaseId = await SeedReleaseAsync("v1.0.0");

        using var client = NewClient();

        // The Operator is refused...
        _factory.BeforeFacadeResolution = () => Demote(OperatorId);
        using (var request = StatusRequest(releaseId, OperatorId, "Planning"))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        // ...while an Admin is served through the same handler with the same body shape — the
        // mutation above touched only the OPERATOR's row.
        using (var request = StatusRequest(releaseId, AdminId, "Planning"))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  4. The other status mappings are untouched by the new Forbidden arm
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE NEW FORBIDDEN ARM DID NOT RESHAPE ANY OTHER STATUS MAPPING: a release that does not
    /// exist still answers 404 <c>{error}</c>, a blank status still answers 400 <c>{error}</c>, and
    /// an invalid status still answers 400 <c>{error}</c> — all for an authorized Operator through
    /// the same handler.
    /// </summary>
    [Fact]
    public async Task StatusPatch_TheOtherStatusMappingsAreUnchanged_ForAnAuthorizedCaller()
    {
        await SeedAccountAsync(OperatorId, UserRole.Operator);

        using var client = NewClient();

        // 404 — the release does not exist.
        using (var request = StatusRequest("no-such-release-xyz", OperatorId, "Planning"))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.True(doc.RootElement.TryGetProperty("error", out _));
        }

        var releaseId = await SeedReleaseAsync("v1.0.0");

        // 400 — a blank status.
        using (var request = StatusRequest(releaseId, OperatorId, ""))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync(Ct);
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("Status is required.", doc.RootElement.GetProperty("error").GetString());
        }

        // 400 — an invalid status.
        using (var request = StatusRequest(releaseId, OperatorId, "Bogus"))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync(Ct);
            using var doc = JsonDocument.Parse(body);
            Assert.Contains("Invalid status", doc.RootElement.GetProperty("error").GetString(),
                StringComparison.Ordinal);
        }
    }

    public void Dispose() => _factory.Dispose();
}
